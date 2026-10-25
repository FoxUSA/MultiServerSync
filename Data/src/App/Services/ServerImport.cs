using System.IO;
using Microsoft.Win32;
using MultiServerSync.Core;

namespace MultiServerSync.Services;

/// <summary>Сервер, найденный в чужих настройках.</summary>
public sealed class ImportCandidate : ObservableObject
{
    private bool _isSelected = true;

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public required string Name { get; init; }

    public required string Host { get; init; }

    public int Port { get; init; } = 22;

    public string User { get; init; } = "root";

    public string KeyPath { get; init; } = string.Empty;

    /// <summary>Откуда взят: «~/.ssh/config», «PuTTY», «WinSCP».</summary>
    public required string Source { get; init; }

    /// <summary>Такой сервер уже есть в программе — по умолчанию не отмечен.</summary>
    public bool Exists { get; init; }

    public string Address => Port == 22 ? Host : $"{Host}:{Port}";

    public string KeyName => KeyPath.Length == 0 ? string.Empty : Path.GetFileName(KeyPath);

    public string Note => Exists ? "уже есть" : string.Empty;
}

/// <summary>
/// Импорт серверов из того, чем человек пользовался до нас: ~/.ssh/config, сессии PuTTY и WinSCP.
/// Пароли не берём: у PuTTY их нет, а у WinSCP они лишь спрятаны, не зашифрованы, — читать их
/// значило бы молча вытащить секреты из чужой программы.
/// </summary>
public static class ServerImport
{
    public static List<ImportCandidate> FindAll()
    {
        var found = new List<ImportCandidate>();

        found.AddRange(Safe("~/.ssh/config", FromSshConfig));
        found.AddRange(Safe("PuTTY", FromPutty));
        found.AddRange(Safe("WinSCP", FromWinScp));

        // одинаковый сервер из нескольких мест — один раз, первым по списку источников
        var unique = found
            .GroupBy(c => Key(c.Host, c.Port, c.User))
            .Select(g => g.First())
            .ToList();

        var known = AppState.Current.Servers
            .Select(s => Key(s.Host, s.Port, s.User))
            .ToHashSet();

        return unique
            .Select(c => known.Contains(Key(c.Host, c.Port, c.User))
                ? new ImportCandidate
                {
                    Name = c.Name, Host = c.Host, Port = c.Port, User = c.User, KeyPath = c.KeyPath,
                    Source = c.Source, Exists = true, IsSelected = false,
                }
                : c)
            .OrderBy(c => c.Exists)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string Key(string host, int port, string user) =>
        $"{host.Trim().ToLowerInvariant()}:{(port <= 0 ? 22 : port)}:{(string.IsNullOrWhiteSpace(user) ? "root" : user.Trim())}";

    /// <summary>Один источник сломан — остальные всё равно читаются.</summary>
    private static IEnumerable<ImportCandidate> Safe(string source, Func<IEnumerable<ImportCandidate>> read)
    {
        try
        {
            return read().ToList();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Импорт: {source} не прочитан — {ex.Message}");
            return [];
        }
    }

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string ExpandPath(string path)
    {
        path = path.Trim().Trim('"');

        if (path.StartsWith('~'))
        {
            path = Home + path[1..];
        }

        return Environment.ExpandEnvironmentVariables(path).Replace('/', Path.DirectorySeparatorChar);
    }

    /// <summary>Блоки Host без шаблонов (* ?): HostName, User, Port, IdentityFile.</summary>
    private static IEnumerable<ImportCandidate> FromSshConfig()
    {
        var path = Path.Combine(Home, ".ssh", "config");

        if (!File.Exists(path))
        {
            yield break;
        }

        string? alias = null;
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in File.ReadLines(path).Append("Host"))
        {
            var line = raw.Trim();

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var cut = line.IndexOfAny([' ', '\t', '=']);
            var key = cut < 0 ? line : line[..cut];
            var value = cut < 0 ? string.Empty : line[(cut + 1)..].Trim().TrimStart('=').Trim();

            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase) || key.Equals("Match", StringComparison.OrdinalIgnoreCase))
            {
                if (alias is not null && Make(alias, fields) is { } made)
                {
                    yield return made;
                }

                // у Host может быть несколько имён — берём первое без шаблона
                alias = key.Equals("Host", StringComparison.OrdinalIgnoreCase)
                    ? value.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(a => !a.Contains('*') && !a.Contains('?'))
                    : null;
                fields.Clear();
                continue;
            }

            fields.TryAdd(key, value);
        }

        static ImportCandidate? Make(string alias, Dictionary<string, string> f)
        {
            var host = f.GetValueOrDefault("HostName", alias);

            return host.Length == 0 ? null : new ImportCandidate
            {
                Name = alias,
                Host = host,
                Port = int.TryParse(f.GetValueOrDefault("Port"), out var port) ? port : 22,
                User = f.GetValueOrDefault("User", "root"),
                KeyPath = f.TryGetValue("IdentityFile", out var key) ? ExpandPath(key) : string.Empty,
                Source = "~/.ssh/config",
            };
        }
    }

    /// <summary>Сохранённые сессии PuTTY: реестр, только протокол SSH.</summary>
    private static IEnumerable<ImportCandidate> FromPutty()
    {
        using var sessions = Registry.CurrentUser.OpenSubKey(@"Software\SimonTatham\PuTTY\Sessions");

        if (sessions is null)
        {
            yield break;
        }

        foreach (var name in sessions.GetSubKeyNames())
        {
            using var s = sessions.OpenSubKey(name);
            var host = s?.GetValue("HostName") as string ?? string.Empty;

            if (s is null || host.Length == 0 || (s.GetValue("Protocol") as string ?? "ssh") != "ssh")
            {
                continue;
            }

            // «user@host» в поле адреса — так тоже бывает
            var user = s.GetValue("UserName") as string ?? string.Empty;

            if (host.Contains('@'))
            {
                user = user.Length == 0 ? host[..host.IndexOf('@')] : user;
                host = host[(host.IndexOf('@') + 1)..];
            }

            yield return new ImportCandidate
            {
                Name = Uri.UnescapeDataString(name),
                Host = host,
                Port = s.GetValue("PortNumber") is int port && port > 0 ? port : 22,
                User = user.Length == 0 ? "root" : user,
                KeyPath = s.GetValue("PublicKeyFile") is string key && key.Length > 0 ? ExpandPath(key) : string.Empty,
                Source = "PuTTY",
            };
        }
    }

    /// <summary>Сайты WinSCP: реестр, а если WinSCP хранит настройки в файле — WinSCP.ini.</summary>
    private static IEnumerable<ImportCandidate> FromWinScp()
    {
        using var sessions = Registry.CurrentUser.OpenSubKey(@"Software\Martin Prikryl\WinSCP 2\Sessions");

        if (sessions is not null)
        {
            foreach (var name in sessions.GetSubKeyNames())
            {
                using var s = sessions.OpenSubKey(name);
                var host = s?.GetValue("HostName") as string ?? string.Empty;

                if (s is null || host.Length == 0)
                {
                    continue;
                }

                yield return new ImportCandidate
                {
                    Name = Uri.UnescapeDataString(name).Replace('/', ' ').Trim(),
                    Host = host,
                    Port = s.GetValue("PortNumber") is int port && port > 0 ? port : 22,
                    User = s.GetValue("UserName") as string is { Length: > 0 } user ? user : "root",
                    KeyPath = s.GetValue("PublicKeyFile") is string key && key.Length > 0 ? ExpandPath(Uri.UnescapeDataString(key)) : string.Empty,
                    Source = "WinSCP",
                };
            }

            yield break;
        }

        var ini = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinSCP.ini"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinSCP", "WinSCP.ini"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WinSCP", "WinSCP.ini"),
        }.FirstOrDefault(File.Exists);

        if (ini is null)
        {
            yield break;
        }

        string? section = null;
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in File.ReadLines(ini).Append("[end]"))
        {
            var line = raw.Trim();

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                if (section is not null && fields.TryGetValue("HostName", out var host) && host.Length > 0)
                {
                    yield return new ImportCandidate
                    {
                        Name = Uri.UnescapeDataString(section).Replace('/', ' ').Trim(),
                        Host = host,
                        Port = int.TryParse(fields.GetValueOrDefault("PortNumber"), out var port) && port > 0 ? port : 22,
                        User = fields.GetValueOrDefault("UserName") is { Length: > 0 } user ? user : "root",
                        KeyPath = fields.GetValueOrDefault("PublicKeyFile") is { Length: > 0 } key ? ExpandPath(Uri.UnescapeDataString(key)) : string.Empty,
                        Source = "WinSCP",
                    };
                }

                var name = line[1..^1];
                section = name.StartsWith(@"Sessions\", StringComparison.OrdinalIgnoreCase) ? name[9..] : null;
                fields.Clear();
                continue;
            }

            var eq = line.IndexOf('=');

            if (section is not null && eq > 0)
            {
                fields[line[..eq]] = line[(eq + 1)..];
            }
        }
    }
}
