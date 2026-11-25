using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using MaxMind.GeoIP2;
using MultiServerSync.Core;
using MultiServerSync.Models;

namespace MultiServerSync.Services;

/// <summary>
/// Гео серверов — страна и город по адресу, из базы GeoLite2-City от MaxMind. База берётся с api.byfox.dev
/// (там она собирается из первоисточника) и лежит в UserData\geo. Новая версия ищется, когда своей базе 30 дней,
/// и по кнопке «Обновить» в «Настройках»; файл сверяется по sha256 до замены. Имя хоста разрешается в адрес здесь же.
/// </summary>
public static class GeoBase
{
    private const string Api = "https://api.byfox.dev/";
    private const string FileName = "GeoLite2-City.mmdb";

    /// <summary>Своей базе столько — пора спросить сайт о новой.</summary>
    private static readonly TimeSpan Age = TimeSpan.FromDays(30);

    /// <summary>Сайт просит не чаще: манифест — не чаще раза в 6 ч, у нас — раз в сутки, если новой не нашлось.</summary>
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(24);

    private static readonly string Dir = Path.Combine(AppPaths.UserDataDir, "geo");
    private static readonly string DbPath = Path.Combine(Dir, FileName);
    private static readonly string StatePath = Path.Combine(Dir, "geo.json");

    private static readonly Lock Gate = new();
    private static DatabaseReader? _reader;
    private static GeoState _state = new();
    private static bool _started;
    private static Task<GeoResult>? _running;

    /// <summary>Дата базы (YYYYMMDD) — для «Настроек»; null — базы ещё нет.</summary>
    public static string? Date => File.Exists(DbPath) ? _state.Date : null;

    /// <summary>Дата базы словами: «22.11.2026».</summary>
    public static string? DateText =>
        Date is { Length: 8 } d ? $"{d[6..8]}.{d[4..6]}.{d[..4]}" : null;

    /// <summary>База появилась или сменилась — «Настройки» и подписи серверов перечитывают.</summary>
    public static event Action? Changed;

    public static async void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;

        // база — 50 МБ в память: в фоне, окно не ждёт
        await Task.Run(Load);
        _state = ReadState();
        Changed?.Invoke();

        AppState.Current.Servers.CollectionChanged += OnServersChanged;
        Follow(AppState.Current.Servers);
        await RefreshAllAsync();

        // проверка — после того как программа открылась; есть база моложе 30 дней — не ходим никуда
        await Task.Delay(TimeSpan.FromSeconds(8));
        var result = await UpdateAsync(force: false);

        if (result.Updated)
        {
            AppLog.General(LogLevel.Info, $"Гео-база обновлена: GeoLite2 от {DateText}.");
        }
    }

    // ── база ─────────────────────────────────────────────────────────────────────

    private static void Load()
    {
        lock (Gate)
        {
            _reader?.Dispose();
            _reader = null;

            if (!File.Exists(DbPath))
            {
                return;
            }

            try
            {
                _reader = new DatabaseReader(DbPath, MaxMind.Db.FileAccessMode.Memory);
            }
            catch (Exception ex)
            {
                AppLog.Debug("Гео-база не открылась: " + ex.Message);
            }
        }
    }

    public sealed record GeoResult(bool Updated, string? Error = null);

    /// <summary>
    /// Новая база с api.byfox.dev, если она есть. <paramref name="force"/> — по кнопке «Обновить»: спросить сайт
    /// сразу; иначе — только когда своей базы нет или ей 30 дней (и не чаще раза в сутки).
    /// <paramref name="progress"/> — проценты загрузки.
    /// </summary>
    public static Task<GeoResult> UpdateAsync(bool force, IProgress<int>? progress = null)
    {
        // идёт уже — ждём ту же загрузку, а не качаем второй раз
        if (_running is { IsCompleted: false } running)
        {
            return running;
        }

        return _running = Task.Run(() => DoUpdateAsync(force, progress));
    }

    private static async Task<GeoResult> DoUpdateAsync(bool force, IProgress<int>? progress)
    {
        var have = File.Exists(DbPath) && _state.Date is not null;

        if (!force && have)
        {
            var due = ParseDate(_state.Date) is not { } date || DateTime.UtcNow - date >= Age;

            if (!due || _state.CheckedAt is { } at && DateTime.UtcNow - at < CheckEvery)
            {
                return new GeoResult(false);
            }
        }

        try
        {
            var http = UpdateService.Client;
            var top = JsonNode.Parse(await http.GetStringAsync(Api + "manifest.json"));
            var remote = top?["geo"]?["geolite2"]?["date"]?.GetValue<string>();

            if (remote is null)
            {
                return new GeoResult(false, "в манифесте api.byfox.dev нет даты GeoLite2");
            }

            if (have && string.CompareOrdinal(remote, _state.Date) <= 0)
            {
                _state.CheckedAt = DateTime.UtcNow;
                WriteState();
                return new GeoResult(false);
            }

            var geo = JsonNode.Parse(await http.GetStringAsync(Api + "geo/manifest.json"));
            var file = geo?["geolite2"]?["files"]?[FileName];
            var sha = file?["sha256"]?.GetValue<string>();
            var size = file?["size"]?.GetValue<long>() ?? 0;

            if (sha is null)
            {
                return new GeoResult(false, "в манифесте гео нет файла " + FileName);
            }

            Directory.CreateDirectory(Dir);
            var part = DbPath + ".part";

            using (var response = await http.GetAsync(Api + "geo/" + FileName, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? size;

                await using var source = await response.Content.ReadAsStreamAsync();
                await using var target = File.Create(part);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long done = 0;
                var shown = -1;
                int read;

                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read));
                    hash.AppendData(buffer, 0, read);
                    done += read;

                    if (total > 0 && (int)(done * 100 / total) is var pct && pct != shown)
                    {
                        shown = pct;
                        progress?.Report(pct);
                    }
                }

                if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(sha, StringComparison.OrdinalIgnoreCase))
                {
                    target.Close();
                    File.Delete(part);
                    return new GeoResult(false, "файл базы не сошёлся по sha256 — оставлена прежняя");
                }
            }

            lock (Gate)
            {
                _reader?.Dispose();
                _reader = null;
                File.Move(part, DbPath, overwrite: true);
            }

            Load();
            _state = new GeoState { Date = remote, CheckedAt = DateTime.UtcNow };
            WriteState();

            await RefreshAllAsync();
            Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke());
            return new GeoResult(true);
        }
        catch (Exception ex)
        {
            AppLog.Debug("Гео-база: не обновилась — " + ex.Message);
            return new GeoResult(false, ex.Message);
        }
    }

    private static DateTime? ParseDate(string? date) =>
        DateTime.TryParseExact(date, "yyyyMMdd", null, System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d.ToUniversalTime() : null;

    // ── гео серверов ─────────────────────────────────────────────────────────────

    private static void OnServersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is { } added)
        {
            var list = added.OfType<ServerEntry>().ToList();
            Follow(list);
            _ = Task.WhenAll(list.Select(RefreshAsync));
        }
    }

    private static void Follow(IEnumerable<ServerEntry> servers)
    {
        foreach (var server in servers)
        {
            server.PropertyChanged -= OnServerChanged;
            server.PropertyChanged += OnServerChanged;
        }
    }

    private static void OnServerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is ServerEntry server && e.PropertyName == nameof(ServerEntry.Host))
        {
            _ = RefreshAsync(server);
        }
    }

    private static Task RefreshAllAsync() => Task.WhenAll(AppState.Current.Servers.ToList().Select(RefreshAsync));

    /// <summary>Страна и город сервера: адрес как есть или имя хоста через DNS; частная сеть — «локальная сеть».</summary>
    private static async Task RefreshAsync(ServerEntry server)
    {
        if (server.IsPseudo || server.IsLocalPc)
        {
            return;
        }

        var host = server.Host.Trim();
        string geo;

        try
        {
            var address = IPAddress.TryParse(host, out var ip)
                ? ip
                : (await Dns.GetHostAddressesAsync(host)).OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).FirstOrDefault();

            geo = address is null ? string.Empty : Lookup(address);
        }
        catch
        {
            // имя не разрешилось (нет сети, опечатка) — подписи нет, а не ошибка
            geo = string.Empty;
        }

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            // пока разрешалось, адрес могли сменить — ответ про прежний не нужен
            if (server.Host.Trim() == host)
            {
                server.Geo = geo;
            }
        });
    }

    private static string Lookup(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || IsPrivate(address))
        {
            return "локальная сеть";
        }

        lock (Gate)
        {
            if (_reader is null || !_reader.TryCity(address, out var city) || city is null)
            {
                return string.Empty;
            }

            static string? Ru(MaxMind.GeoIP2.Model.NamedEntity entity) =>
                entity.Names.TryGetValue("ru", out var ru) && !string.IsNullOrWhiteSpace(ru) ? ru : entity.Name;

            var country = Ru(city.Country);
            var town = Ru(city.City);
            return string.Join(", ", new[] { country, town }.Where(s => !string.IsNullOrWhiteSpace(s)));
        }
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal;
        }

        var b = address.GetAddressBytes();
        return b[0] == 10
            || b[0] == 172 && b[1] >= 16 && b[1] <= 31
            || b[0] == 192 && b[1] == 168
            || b[0] == 169 && b[1] == 254
            || b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }

    // ── память базы ──────────────────────────────────────────────────────────────

    private sealed class GeoState
    {
        /// <summary>Дата GeoLite2 (YYYYMMDD) — как в манифесте api.byfox.dev.</summary>
        public string? Date { get; set; }

        /// <summary>Когда последний раз спрашивали сайт.</summary>
        public DateTime? CheckedAt { get; set; }
    }

    private static GeoState ReadState()
    {
        try
        {
            return File.Exists(StatePath) ? JsonSerializer.Deserialize<GeoState>(File.ReadAllText(StatePath)) ?? new() : new();
        }
        catch
        {
            return new();
        }
    }

    private static void WriteState()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            AtomicFile.Write(StatePath, JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            AppLog.Debug("Гео-база: не записалась geo.json — " + ex.Message);
        }
    }
}
