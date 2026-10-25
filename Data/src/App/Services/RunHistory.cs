using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using MultiServerSync.Core;
using MultiServerSync.Models;

namespace MultiServerSync.Services;

/// <summary>Один запуск: когда и на каких серверах.</summary>
public sealed class RunEvent
{
    public DateTime At { get; set; }

    public List<string> Servers { get; set; } = new();
}

public sealed class RunHistoryFile
{
    /// <summary>Ключ действия → последние запуски, свежие в конце.</summary>
    public Dictionary<string, List<RunEvent>> Runs { get; set; } = new();
}

/// <summary>
/// Умный режим: помнит, на каких серверах запускалась каждая команда и копирование с каждой
/// вкладки, и перед запуском спрашивает, если сейчас отмечен сервер, непривычный для этого действия.
///
/// Правило. Вес запуска — 0,5^(возраст в днях / 14): двухнедельной давности весит вдвое меньше.
/// «Привычный» сервер — тот, на который пришлось не меньше 70 % веса всех запусков. Спрашиваем,
/// только когда запусков не меньше трёх, привычный сервер есть, а среди отмеченных есть такой,
/// который не привычный и на котором это действие не запускали последние 14 дней.
/// Если ходить на два сервера попеременно, привычного нет — тишина. «Выполнить» записывает
/// запуск, и на следующий раз этот сервер уже знаком.
///
/// Команды, которые только читают (ls, df, docker ps, systemctl status…), не проверяются
/// и в историю не пишутся: на любом сервере они безопасны.
///
/// История пишется всегда, даже когда режим выключен: включённый режим работает сразу.
/// </summary>
public static class RunHistory
{
    /// <summary>Сколько запусков помнить на одно действие.</summary>
    private const int Keep = 20;

    /// <summary>Меньше — истории не хватает для вывода.</summary>
    private const int MinRuns = 3;

    /// <summary>Доля веса, с которой сервер считается привычным.</summary>
    private const double UsualShare = 0.7;

    /// <summary>Период полураспада веса и он же «недавно запускали».</summary>
    private const double HalfLifeDays = 14;

    /// <summary>Старше — забываем: дальше история уже не про сегодняшнюю работу.</summary>
    private const int ForgetDays = 180;

    /// <summary>Короткие команды (ls, df, top) гоняют везде — следить за ними незачем.</summary>
    private const int MinCommandLength = 4;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    private static RunHistoryFile? _file;

    /// <summary>
    /// Вопрос пользователю посреди окна: заголовок, пояснение и, если нужна, третья кнопка.
    /// Ответ: 0 — отмена, 1 — основная кнопка, 2 — третья. Ставит главное окно.
    /// </summary>
    public static Func<string, string, string, string?, int>? Choose { get; set; }

    private static RunHistoryFile File => _file ??= Load();

    /// <summary>
    /// Проверка команд одной вкладки — одно окно или цепочка окон. Ничего не спрашивает
    /// и не пишет: спросить — <see cref="Confirm"/>, записать — <see cref="Commit"/>.
    /// </summary>
    /// <param name="steps">Номер окна (0 — окно одно, номер не нужен) и его текст.</param>
    public static SmartCheck CheckCommands(Workspace tab, IEnumerable<(int Number, string Text)> steps, IReadOnlyList<ServerEntry> targets)
    {
        var check = new SmartCheck(tab.Title, targets);

        foreach (var (number, command) in steps)
        {
            var text = Normalize(command);

            // «только посмотреть» безопасно на любом сервере: не спрашиваем и в историю не пишем.
            // Смотрим исходный текст: в нормализованном переносы строк уже стали пробелами
            if (text.Length < MinCommandLength || IsReadOnly(command))
            {
                continue;
            }

            var tabKey = $"cmd:{tab.Id}:{text}";
            var anyKey = $"cmd:*:{text}";
            check.Keys.Add((tabKey, check.Targets));
            check.Keys.Add((anyKey, check.Targets));

            // у вкладки своя история точнее; у новой вкладки её нет — тогда по всем вкладкам
            var key = Events(tabKey).Count >= MinRuns ? tabKey : anyKey;

            if (Evaluate(key, targets) is { } odd)
            {
                var what = number > 0 ? $"#{number} {Preview(command)}" : Preview(command);
                check.Add(odd, what, "запусков");
            }
        }

        return check;
    }

    /// <summary>Проверка копирования с вкладки. Ничего не спрашивает и не пишет.</summary>
    public static SmartCheck CheckCopy(Workspace tab, IReadOnlyList<ServerEntry> targets)
    {
        var check = new SmartCheck(tab.Title, targets);
        var key = $"copy:{tab.Id}";
        check.Keys.Add((key, check.Targets));

        if (Evaluate(key, targets) is { } odd)
        {
            check.Add(odd, "копирование", "копирований");
        }

        return check;
    }

    /// <summary>
    /// Спросить по одной вкладке, если есть что спрашивать. true — запускать.
    /// Выключенный режим и пустая проверка не спрашивают.
    /// </summary>
    public static bool Confirm(SmartCheck check)
    {
        if (!check.HasWarnings || Choose is null)
        {
            return true;
        }

        var (head, detail) = check.Warnings.Count == 1
            ? (check.Warnings[0].Headline, check.Warnings[0].Detail + $"\nВкладка «{check.Tab}».")
            : ("Непривычные серверы — проверьте перед запуском",
                string.Join("\n", check.Warnings.Select(w => "• " + w.Line)) + $"\nВкладка «{check.Tab}».");

        AppLog.Info($"Умный режим: {head}.", check.Tab);

        if (Choose(head, detail, "Выполнить", null) == 1)
        {
            AppLog.Info("Умный режим: выполнено по подтверждению.", check.Tab);
            return true;
        }

        AppLog.Info("Умный режим: запуск отменён.", check.Tab);
        return false;
    }

    /// <summary>
    /// «Выполнить везде»: одно окно на все вкладки вместо череды. Возвращает вкладки, которые
    /// запускать, или null — отменили всё. «Пропустить» запускает все, кроме подозрительных.
    /// </summary>
    public static List<T>? ConfirmMany<T>(IReadOnlyList<(T Tab, SmartCheck? Check)> checks)
    {
        var warned = checks.Where(c => c.Check is { HasWarnings: true }).ToList();

        if (warned.Count == 0 || Choose is null)
        {
            return checks.Select(c => c.Tab).ToList();
        }

        var lines = warned
            .SelectMany(c => c.Check!.Warnings.Select(w => $"• «{c.Check.Tab}» — {w.Line}"))
            .Take(12)
            .ToList();

        var more = warned.Sum(c => c.Check!.Warnings.Count) - lines.Count;

        if (more > 0)
        {
            lines.Add($"…и ещё {more}");
        }

        var head = warned.Count == 1
            ? "На одной вкладке непривычный сервер"
            : $"На {warned.Count} вкладках непривычные серверы";

        AppLog.Info($"Умный режим: {head}.");

        switch (Choose(head, string.Join("\n", lines), "Выполнить всё", $"Пропустить эти ({warned.Count})"))
        {
            case 1:
                AppLog.Info("Умный режим: выполнено везде по подтверждению.");
                return checks.Select(c => c.Tab).ToList();

            case 2:
                AppLog.Info($"Умный режим: вкладки с непривычными серверами пропущены ({warned.Count}).");
                return checks.Where(c => c.Check is not { HasWarnings: true }).Select(c => c.Tab).ToList();

            default:
                AppLog.Info("Умный режим: запуск отменён.");
                return null;
        }
    }

    /// <summary>Запуск состоялся — записываем: на следующий раз эти серверы уже знакомы.</summary>
    public static void Commit(SmartCheck? check)
    {
        if (check is null || check.Keys.Count == 0)
        {
            return;
        }

        foreach (var (key, targets) in check.Keys.DistinctBy(k => k.Key))
        {
            Record(key, targets);
        }

        Save();
    }

    /// <summary>Чем этот запуск непривычен — или null, если всё как обычно или судить не по чему.</summary>
    private static Oddity? Evaluate(string key, IReadOnlyList<ServerEntry> targets)
    {
        if (!AppState.Current.Settings.SmartMode)
        {
            return null;
        }

        var known = AppState.Current.Servers.ToDictionary(s => s.Id);
        var now = DateTime.Now;

        // удалённые серверы в расчёт не идут: о них не спросишь и их не отметишь
        var events = Events(key)
            .Select(e => (e.At, Servers: e.Servers.Where(known.ContainsKey).ToList()))
            .Where(e => e.Servers.Count > 0)
            .ToList();

        if (events.Count < MinRuns)
        {
            return null;
        }

        var total = 0.0;
        var weights = new Dictionary<string, double>();
        var recent = new HashSet<string>();
        var ever = new HashSet<string>();

        foreach (var (at, servers) in events)
        {
            var age = Math.Max(0, (now - at).TotalDays);
            var weight = Math.Pow(0.5, age / HalfLifeDays);
            total += weight;

            foreach (var id in servers)
            {
                weights[id] = weights.GetValueOrDefault(id) + weight;
                ever.Add(id);

                if (age <= HalfLifeDays)
                {
                    recent.Add(id);
                }
            }
        }

        var usual = weights
            .Where(p => p.Value / total >= UsualShare)
            .OrderByDescending(p => p.Value)
            .Select(p => known[p.Key])
            .ToList();

        if (usual.Count == 0)
        {
            return null;
        }

        var strangers = targets
            .Where(t => !usual.Any(u => u.Id == t.Id) && !recent.Contains(t.Id))
            .ToList();

        if (strangers.Count == 0)
        {
            return null;
        }

        return new Oddity(
            usual,
            strangers,
            events.Count,
            events.Count(e => e.Servers.Contains(usual[0].Id)),
            strangers.All(s => !ever.Contains(s.Id)));
    }

    private static string Preview(string command)
    {
        var line = command.Trim().Replace("\r\n", "\n").Split('\n')[0];
        return line.Length > 60 ? line[..60] + "…" : line;
    }

    private static string Names(IReadOnlyList<ServerEntry> servers)
    {
        var shown = string.Join(", ", servers.Take(3).Select(s => $"«{s.PaneLabel}»"));
        return servers.Count > 3 ? $"{shown} и ещё {servers.Count - 3}" : shown;
    }

    /// <summary>Что непривычно: где обычно, где сейчас, и насколько уверенно.</summary>
    internal sealed record Oddity(
        IReadOnlyList<ServerEntry> Usual,
        IReadOnlyList<ServerEntry> Strangers,
        int Runs,
        int Hits,
        bool Never);

    /// <summary>Итог проверки одной вкладки: что спросить и что записать после запуска.</summary>
    public sealed class SmartCheck(string tab, IReadOnlyList<ServerEntry> targets)
    {
        public string Tab { get; } = tab;

        public IReadOnlyList<ServerEntry> Targets { get; } = targets;

        public List<SmartWarning> Warnings { get; } = [];

        public bool HasWarnings => Warnings.Count > 0;

        /// <summary>Что записать в историю и на каких серверах это выполнится.</summary>
        internal List<(string Key, IReadOnlyList<ServerEntry> Targets)> Keys { get; } = [];

        /// <summary>Связка «копирование → команды»: обе проверки — одним вопросом и одной записью.</summary>
        public SmartCheck With(SmartCheck? other)
        {
            if (other is not null)
            {
                Warnings.AddRange(other.Warnings);
                Keys.AddRange(other.Keys);
            }

            return this;
        }

        internal void Add(Oddity odd, string what, string runsWord)
        {
            var now = odd.Strangers.Count == 1
                ? $"сейчас отмечен {Names(odd.Strangers)}"
                : $"сейчас отмечены {Names(odd.Strangers)}";
            var why = odd.Never
                ? "здесь на нём такого ещё не запускали"
                : $"здесь на нём такого не запускали последние {HalfLifeDays:0} дней";

            Warnings.Add(new SmartWarning(
                $"{what}: обычно на {Names(odd.Usual)}, {now}",
                what == "копирование"
                    ? $"Обычно с этой вкладки копируют на {Names(odd.Usual)}"
                    : $"Обычно эта команда выполняется на {Names(odd.Usual)}",
                char.ToUpper(now[0]) + now[1..] + " — " + why + ".\n"
                    + $"Из последних {odd.Runs} {runsWord} на «{odd.Usual[0].PaneLabel}» — {odd.Hits}."
                    + (what == "копирование" ? string.Empty : $"\nКоманда: {what}")));
        }
    }

    /// <summary>Одно непривычное место: строкой для списка и заголовком с пояснением для одиночного вопроса.</summary>
    public sealed record SmartWarning(string Line, string Headline, string Detail);

    /// <summary>Команды, которые только читают, — с любыми аргументами.</summary>
    private static readonly HashSet<string> ReadVerbs =
    [
        "ls", "ll", "la", "cat", "head", "tail", "less", "more", "grep", "egrep", "fgrep", "zgrep", "rg",
        "df", "du", "free", "uptime", "uname", "whoami", "id", "hostname", "date", "ps", "pwd", "echo",
        "printf", "stat", "wc", "which", "whereis", "type", "file", "lsblk", "lscpu", "lsof", "lsmod",
        "lspci", "lsusb", "nproc", "ss", "netstat", "last", "lastlog", "w", "who", "printenv",
        "tree", "sort", "uniq", "cut", "tr", "awk", "column", "cd", "history", "dmesg", "vmstat",
        "iostat", "mpstat", "sensors", "ping", "dig", "nslookup", "host", "traceroute", "getent",
        "groups", "cal", "md5sum", "sha1sum", "sha256sum", "readlink", "realpath", "basename", "dirname",
        "nl", "diff", "cmp", "jq", "test", "true", "top", "htop", "apt-cache", "find", "journalctl",
    ];

    /// <summary>
    /// Флаги, с которыми «смотрящая» команда уже меняет: find удаляет и запускает, date ставит
    /// время, dmesg чистит буфер, sort и tree пишут в файл, rg запускает препроцессор.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> VerbWriteFlags = new()
    {
        ["find"] = ["-delete", "-exec", "-execdir", "-ok", "-okdir", "-fprint", "-fprint0", "-fprintf", "-fls"],
        ["journalctl"] = ["--vacuum-size", "--vacuum-time", "--vacuum-files", "--rotate", "--flush", "--sync", "--relinquish-var"],
        ["date"] = ["-s", "--set"],
        ["dmesg"] = ["-c", "-C", "--clear", "--read-clear"],
        ["ss"] = ["-K", "--kill"],
        ["sort"] = ["-o"],
        ["tree"] = ["-o"],
        ["rg"] = ["--pre"],
        ["history"] = ["-c", "-d", "-w", "-a", "-r", "-n"],
    };

    /// <summary>Команды, которые только читают с этими подкомандами (первый аргумент не флаг).</summary>
    private static readonly Dictionary<string, HashSet<string>> ReadSubcommands = new()
    {
        ["systemctl"] = ["status", "is-active", "is-enabled", "is-failed", "list-units", "list-unit-files", "list-timers", "show", "cat"],
        ["docker"] = ["ps", "logs", "images", "inspect", "stats", "version", "info", "top", "port", "diff"],
        ["podman"] = ["ps", "logs", "images", "inspect", "stats", "version", "info", "top", "port", "diff"],
        ["docker-compose"] = ["ps", "logs", "config", "images", "top", "ls"],
        ["git"] = ["status", "log", "diff", "show", "remote", "rev-parse", "describe", "blame"],
        ["apt"] = ["list", "show", "search", "policy"],
        ["ufw"] = ["status"],
        ["nft"] = ["list"],
        ["certbot"] = ["certificates"],
        ["ip"] = ["a", "addr", "address", "r", "route", "l", "link", "n", "neigh"],
        ["timedatectl"] = ["status", "show"],
        ["hostnamectl"] = ["status"],
    };

    /// <summary>Подкоманды ip, которые меняют сеть: ip addr add, ip link set…</summary>
    private static readonly HashSet<string> IpWriteWords =
        ["add", "del", "delete", "set", "flush", "change", "replace", "append", "prepend"];

    /// <summary>
    /// Команды, которые только читают с одним из ключевых флагов, — и тогда все прочие флаги
    /// должны быть из безобидных. iptables -L -n читает, iptables -A … -s адрес — уже правило.
    /// </summary>
    private static readonly Dictionary<string, (HashSet<string> Key, HashSet<string> Extra)> ReadFlags = new()
    {
        ["nginx"] = (["-t", "-T", "-v", "-V"], ["-c", "-q"]),
        ["dpkg"] = (["-l", "-L", "-s", "-S", "--list", "--listfiles", "--status", "--search"], []),
        ["iptables"] = (["-L", "-S", "--list", "--list-rules"], ["-n", "-v", "-x", "-t", "--numeric", "--verbose", "--exact", "--line-numbers"]),
        ["ip6tables"] = (["-L", "-S", "--list", "--list-rules"], ["-n", "-v", "-x", "-t", "--numeric", "--verbose", "--exact", "--line-numbers"]),
        ["crontab"] = (["-l"], ["-u"]),
    };

    /// <summary>
    /// Команда только читает: каждая её часть (через ; &amp;&amp; || | и перенос строки) —
    /// из белого списка, нет записи в файл и подстановки команд. Сомнение — значит меняет:
    /// лишний вопрос дешевле пропущенного.
    /// </summary>
    private static bool IsReadOnly(string text)
    {
        // вывод в никуда и слияние потоков файлов не пишут
        var line = Regex.Replace(text, @"\d?&?>>?\s*/dev/null|\d>&\d", " ");

        if (line.Contains('>') || line.Contains("$(") || line.Contains('`'))
        {
            return false;
        }

        foreach (var part in Regex.Split(line, @"\|\||&&|[;|&\n]"))
        {
            var words = Regex.Split(part.Trim(), @"\s+").Where(w => w.Length > 0).ToList();

            // строка-комментарий
            if (words.Count > 0 && words[0].StartsWith('#'))
            {
                continue;
            }

            // переменные окружения перед командой
            while (words.Count > 0 && Regex.IsMatch(words[0], @"^[a-z_][a-z0-9_]*=", RegexOptions.IgnoreCase))
            {
                words.RemoveAt(0);
            }

            // sudo сам по себе ничего не меняет; с флагами (-u, -i…) — уже не угадать
            if (words.Count > 0 && words[0] == "sudo")
            {
                words.RemoveAt(0);

                if (words.Count > 0 && words[0].StartsWith('-'))
                {
                    return false;
                }
            }

            if (words.Count == 0)
            {
                continue;
            }

            if (!IsReadOnlyPart(words))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsReadOnlyPart(List<string> words)
    {
        var verb = words[0][(words[0].LastIndexOf('/') + 1)..];
        var args = words.Skip(1).ToList();
        var flags = args.Where(a => a.StartsWith('-')).ToList();
        var positional = args.Where(a => !a.StartsWith('-')).ToList();

        if (VerbWriteFlags.TryGetValue(verb, out var writes)
            && flags.Any(f => writes.Contains(f) || writes.Contains(f.Split('=')[0])))
        {
            return false;
        }

        // hostname имя — переименование; uniq вход выход — запись в файл; awk умеет system()
        if ((verb == "hostname" && positional.Count > 0)
            || (verb == "uniq" && positional.Count > 1)
            || (verb == "awk" && args.Any(a => a.Contains("system") || a.Contains("getline"))))
        {
            return false;
        }

        if (ReadVerbs.Contains(verb) || verb == "hostname")
        {
            return true;
        }

        if (verb == "ip" && args.Any(IpWriteWords.Contains))
        {
            return false;
        }

        var sub = positional.FirstOrDefault();

        // docker compose ps — подкоманда у подкоманды
        if (verb is "docker" or "podman" && sub == "compose")
        {
            return args.SkipWhile(a => a != "compose").Skip(1).FirstOrDefault(a => !a.StartsWith('-'))
                is { } inner && ReadSubcommands["docker-compose"].Contains(inner);
        }

        if (ReadSubcommands.TryGetValue(verb, out var subs))
        {
            return sub is not null && subs.Contains(sub);
        }

        if (ReadFlags.TryGetValue(verb, out var known))
        {
            return flags.Any(known.Key.Contains)
                && flags.All(f => known.Key.Contains(f) || known.Extra.Contains(f));
        }

        return false;
    }

    /// <summary>Лишние пробелы и регистр не делают команду другой.</summary>
    private static string Normalize(string command) =>
        Regex.Replace(command.Trim(), @"\s+", " ").ToLowerInvariant();

    private static List<RunEvent> Events(string key) =>
        File.Runs.TryGetValue(key, out var list) ? list : new List<RunEvent>();

    private static void Record(string key, IReadOnlyList<ServerEntry> targets)
    {
        if (!File.Runs.TryGetValue(key, out var list))
        {
            list = new List<RunEvent>();
            File.Runs[key] = list;
        }

        list.Add(new RunEvent { At = DateTime.Now, Servers = targets.Select(t => t.Id).ToList() });

        if (list.Count > Keep)
        {
            list.RemoveRange(0, list.Count - Keep);
        }
    }

    private static RunHistoryFile Load()
    {
        RunHistoryFile? file = null;

        try
        {
            if (System.IO.File.Exists(AppPaths.RunHistoryFile))
            {
                file = JsonSerializer.Deserialize<RunHistoryFile>(
                    System.IO.File.ReadAllText(AppPaths.RunHistoryFile), JsonOptions);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"Не удалось прочитать {Path.GetFileName(AppPaths.RunHistoryFile)}: {ex.Message}");
        }

        file ??= new RunHistoryFile();

        // старое забываем сразу при чтении, чтобы файл не рос годами
        var border = DateTime.Now.AddDays(-ForgetDays);

        foreach (var key in file.Runs.Keys.ToList())
        {
            file.Runs[key].RemoveAll(e => e.At < border);

            if (file.Runs[key].Count == 0)
            {
                file.Runs.Remove(key);
            }
        }

        return file;
    }

    private static void Save()
    {
        try
        {
            var tmp = AppPaths.RunHistoryFile + ".tmp";
            System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(File, JsonOptions));
            System.IO.File.Move(tmp, AppPaths.RunHistoryFile, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Не удалось сохранить {Path.GetFileName(AppPaths.RunHistoryFile)}: {ex.Message}");
        }
    }
}
