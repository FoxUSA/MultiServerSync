using System.Collections.Concurrent;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using MultiServerSync.Core;
using MultiServerSync.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace MultiServerSync.Services;

/// <summary>Запомненный ключ сервера.</summary>
public sealed class KnownHostKey
{
    public string Algorithm { get; set; } = string.Empty;

    /// <summary>«SHA256:…» — как его показывает ssh.</summary>
    public string Fingerprint { get; set; } = string.Empty;

    public DateTime Added { get; set; }
}

/// <summary>
/// Проверка ключа сервера, как known_hosts у ssh. Первое подключение запоминает ключ без
/// вопроса — на полусотне серверов полсотни окон никто читать не станет. Ключ сменился —
/// подключение обрывается до входа: пароль подменённому серверу не уходит. Принять новый
/// ключ можно в окне сервера, если сервер переустанавливали.
/// </summary>
public static class HostKeys
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly object Gate = new();

    /// <summary>Ключ, который сервер показал вместо запомненного: «адрес:порт» → новый ключ.</summary>
    private static readonly ConcurrentDictionary<string, KnownHostKey> Mismatches = new();

    private static Dictionary<string, KnownHostKey>? _known;

    private static Dictionary<string, KnownHostKey> Known
    {
        get
        {
            lock (Gate)
            {
                return _known ??= Load();
            }
        }
    }

    /// <summary>Подключение с проверкой ключа: так создаётся любой клиент SSH и SFTP.</summary>
    public static T Watch<T>(T client, ServerEntry server)
        where T : BaseClient
    {
        var id = Id(server);
        var label = server.PaneLabel;
        client.HostKeyReceived += (_, e) => e.CanTrust = Check(id, label, e);
        return client;
    }

    /// <summary>Запомненный отпечаток сервера, если он есть.</summary>
    public static KnownHostKey? Get(ServerEntry server)
    {
        lock (Gate)
        {
            return Known.GetValueOrDefault(Id(server));
        }
    }

    /// <summary>Сервер показал другой ключ, чем запомнен: какой именно.</summary>
    public static KnownHostKey? Mismatch(ServerEntry server) => Mismatches.GetValueOrDefault(Id(server));

    /// <summary>Забыть ключ: следующее подключение запомнит тот, что покажет сервер.</summary>
    public static void Forget(ServerEntry server)
    {
        var id = Id(server);

        lock (Gate)
        {
            if (!Known.Remove(id))
            {
                return;
            }

            Save();
        }

        Mismatches.TryRemove(id, out _);
        AppLog.Info($"Ключ сервера забыт: {server.PaneLabel}. Следующее подключение запомнит новый.");
    }

    /// <summary>Принять ключ, который сервер показывает сейчас вместо запомненного.</summary>
    public static void AcceptNew(ServerEntry server)
    {
        var id = Id(server);

        if (!Mismatches.TryRemove(id, out var fresh))
        {
            return;
        }

        lock (Gate)
        {
            Known[id] = fresh;
            Save();
        }

        AppLog.Info($"Принят новый ключ сервера {server.PaneLabel}: {fresh.Fingerprint}.");
    }

    private static bool Check(string id, string label, HostKeyEventArgs e)
    {
        var seen = new KnownHostKey
        {
            Algorithm = e.HostKeyName,
            Fingerprint = "SHA256:" + e.FingerPrintSHA256,
            Added = DateTime.Now,
        };

        lock (Gate)
        {
            if (!Known.TryGetValue(id, out var known))
            {
                Known[id] = seen;
                Save();
                AppLog.Info($"Ключ сервера запомнен: {seen.Algorithm} {seen.Fingerprint}.", label);
                return true;
            }

            if (known.Fingerprint == seen.Fingerprint)
            {
                Mismatches.TryRemove(id, out _);
                return true;
            }
        }

        Mismatches[id] = seen;
        return false;
    }

    /// <summary>Как у ssh: ключ привязан к адресу и порту, а не к записи в списке.</summary>
    private static string Id(ServerEntry server) =>
        $"{server.Host.Trim().ToLowerInvariant()}:{(server.Port <= 0 ? 22 : server.Port)}";

    private static Dictionary<string, KnownHostKey> Load()
    {
        try
        {
            if (File.Exists(AppPaths.KnownHostsFile))
            {
                return JsonSerializer.Deserialize<Dictionary<string, KnownHostKey>>(
                    File.ReadAllText(AppPaths.KnownHostsFile), JsonOptions) ?? new();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"Не удалось прочитать {Path.GetFileName(AppPaths.KnownHostsFile)}: {ex.Message}");
        }

        return new();
    }

    /// <summary>Вызывать под замком.</summary>
    private static void Save()
    {
        try
        {
            AtomicFile.Write(AppPaths.KnownHostsFile, JsonSerializer.Serialize(_known, JsonOptions));
        }
        catch (Exception ex)
        {
            AppLog.Error($"Не удалось сохранить {Path.GetFileName(AppPaths.KnownHostsFile)}: {ex.Message}");
        }
    }
}
