using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MultiServerSync.Services;

/// <summary>
/// Чужие источники истории «Мониторинга» — те, что на сервере уже собираются: sysstat (sadf) и rudog (Redis).
/// Всё переводится в те же точки, что пишет свой сборщик, — дальше отчёт, итоги и шаг графика те же.
/// Команды — на простом sh, вывод ограничен: большой файл или много дней не подвешивают ни сервер, ни окно.
/// </summary>
public static class MonitorHistory
{
    // ── sysstat ─────────────────────────────────────────────────────────────────

    /// <summary>Сколько строк одного отчёта sadf за день берём самое большее: раз в минуту и много дисков — и то хватает.</summary>
    private const int MaxRowsPerReport = 20000;

    /// <summary>Сколько файлов дней читаем за один заход: остальное — следующими заходами, окно уже показывает прочитанное.</summary>
    public const int FilesPerRequest = 4;

    /// <summary>
    /// Список файлов дней sysstat моложе <paramref name="days"/> суток: путь, размер, время изменения. Имена saDD и
    /// saYYYYMMDD (при HISTORY больше 28 — в подпапках YYYYMM); сжатые (.gz, .xz) и сводки sarDD не берём.
    /// Первой строкой — сдвиг часового пояса сервера: им переводится время, если sadf напишет его не в UTC.
    /// </summary>
    public static string ListCommand(string dir, int days) =>
        "echo \"@tz $(date +%z 2>/dev/null)\"\n"
        + "for f in $(find " + SshService.ShellQuote(dir) + " -maxdepth 2 -name 'sa[0-9]*' ! -name '*.*' -mtime -"
        + days.ToString(CultureInfo.InvariantCulture) + " 2>/dev/null); do "
        + "[ -r \"$f\" ] && echo \"@f $f $(stat -c '%s %Y' \"$f\" 2>/dev/null || echo '0 0')\"; done\n"
        + "echo '@end'\n";

    /// <summary>
    /// Чтение файлов дней: по каждому — процессор, память, сеть по интерфейсам, диски. Диски — с -p (имена sda, nvme0n1);
    /// старый sadf без -p — как есть (dev8-0, переводится по /sys/block). Ошибки sadf («Invalid system activity file»
    /// после обновления пакета, «Requested activities not available» без сбора дисков) идут в вывод — по ним день
    /// или отчёт честно пропускается.
    /// </summary>
    public static string ReadCommand(IEnumerable<string> files)
    {
        var sb = new StringBuilder();
        var limit = MaxRowsPerReport.ToString(CultureInfo.InvariantCulture);

        sb.Append("for f in ").Append(string.Join(" ", files.Select(SshService.ShellQuote))).Append("; do\n");
        sb.Append("echo \"@sa $f\"\n");
        sb.Append("echo '@o u'; sadf -d \"$f\" -- -u 2>&1 | head -n ").Append(limit).Append('\n');
        sb.Append("echo '@o r'; sadf -d \"$f\" -- -r 2>&1 | head -n ").Append(limit).Append('\n');
        sb.Append("echo '@o n'; sadf -d \"$f\" -- -n DEV 2>&1 | head -n ").Append(limit).Append('\n');
        sb.Append("echo '@o d'; { sadf -d \"$f\" -- -d -p 2>/dev/null || sadf -d \"$f\" -- -d 2>&1; } | head -n ").Append(limit).Append('\n');
        sb.Append("done\necho '@end'\n");
        return sb.ToString();
    }

    /// <summary>Файл дня из списка: путь и подпись (размер + время) — по ней прочитанный день не перечитывается.</summary>
    public sealed record SaFile(string Path, string Sig, long Mtime);

    public static (List<SaFile> Files, int TzOffset) ParseList(string output)
    {
        var files = new List<SaFile>();
        var tz = 0;

        foreach (var raw in output.Replace("\r", string.Empty).Split('\n'))
        {
            var line = raw.Trim();

            if (line.StartsWith("@tz ", StringComparison.Ordinal))
            {
                tz = Offset(line[4..].Trim());
            }
            else if (line.StartsWith("@f ", StringComparison.Ordinal))
            {
                var f = line[3..].Split(' ', StringSplitOptions.RemoveEmptyEntries);

                if (f.Length >= 3)
                {
                    files.Add(new SaFile(f[0], f[1] + " " + f[2], long.TryParse(f[2], out var m) ? m : 0));
                }
            }
        }

        return (files, tz);
    }

    /// <summary>«+0300» → секунды; нет или не разобрать — ноль.</summary>
    private static int Offset(string z)
    {
        if (z.Length != 5 || (z[0] != '+' && z[0] != '-')
            || !int.TryParse(z.AsSpan(1, 2), out var h) || !int.TryParse(z.AsSpan(3, 2), out var m))
        {
            return 0;
        }

        return (z[0] == '-' ? -1 : 1) * (h * 3600 + m * 60);
    }

    /// <summary>Один прочитанный день: точки и что в нём вообще было.</summary>
    public sealed class SaDay
    {
        public List<MonitorPoint> Points { get; } = new();

        public bool Cpu { get; set; }

        public bool Mem { get; set; }

        public bool Net { get; set; }

        public bool Disk { get; set; }

        /// <summary>sadf не прочитал файл: другая версия или испорчен — день пропускается.</summary>
        public bool Invalid { get; set; }

        /// <summary>Самый крупный интервал замеров дня, секунды.</summary>
        public int Interval { get; set; }
    }

    /// <summary>
    /// С чем переводить строки sadf в наши точки: всего памяти (из замера «Сейчас»), интерфейс маршрута,
    /// целые диски и «major:minor», сдвиг пояса сервера.
    /// </summary>
    public sealed record SaContext(long MemTotal, string Iface, IReadOnlySet<string> Blocks, IReadOnlyDictionary<string, string> DevNames, int TzOffset);

    private sealed class Acc
    {
        public int Interval;
        public double? Cpu;
        public double? AvailKb;
        public double Rx;
        public double Tx;
        public bool Net;
        public double Rd;
        public double Wr;
        public bool Disk;
    }

    private static readonly string[] SkipDisks = ["loop", "ram", "zram", "dm-", "sr", "fd", "md"];

    /// <summary>
    /// Разбор вывода <see cref="ReadCommand"/>. Столбцы — по именам из строки заголовка «# hostname;interval;timestamp;…»,
    /// а не по номерам: у старых sysstat их другой набор. Формулы — те же, что у «Сейчас»:
    /// процессор = 100 − %idle − %iowait; память = всего − доступно (нет kbavail — свободно + буферы + кэш, как
    /// у старых ядер); сеть — интерфейс маршрута по умолчанию (не знаем его — все, кроме lo); диски — только целые,
    /// без loop, zram, dm-, md, sr; КБ ×1024, секторы ×512. Пиков у sysstat нет — пик равен среднему за интервал.
    /// Время точки — начало интервала (sadf пишет конец), в UTC.
    /// </summary>
    public static Dictionary<string, SaDay> Parse(string output, SaContext context)
    {
        var days = new Dictionary<string, SaDay>(StringComparer.Ordinal);
        SaDay? day = null;
        Dictionary<long, Acc>? rows = null;
        var report = string.Empty;
        string[] header = [];
        var hadRows = false;

        void Close()
        {
            if (day is null || rows is null)
            {
                return;
            }

            foreach (var (end, a) in rows.OrderBy(r => r.Key))
            {
                var interval = Math.Max(1, a.Interval);
                var mem = a.AvailKb is { } avail && context.MemTotal > 0 ? Math.Max(0, context.MemTotal - (long)(avail * 1024)) : 0;

                day.Points.Add(new MonitorPoint
                {
                    T = end - interval,
                    N = Math.Max(1, interval / MonitorProbe.SampleSeconds),
                    Cpu = a.Cpu ?? 0,
                    CpuMax = a.Cpu ?? 0,
                    Mem = mem,
                    MemMax = mem,
                    Rx = a.Rx,
                    RxMax = a.Rx,
                    Tx = a.Tx,
                    TxMax = a.Tx,
                    Rd = a.Rd,
                    RdMax = a.Rd,
                    Wr = a.Wr,
                    WrMax = a.Wr,
                });
                day.Interval = Math.Max(day.Interval, interval);
            }

            // ни одной строки и sadf ругался — день не читается этой версией sadf
            day.Invalid = day.Points.Count == 0 && day.Invalid;
        }

        foreach (var raw in output.Replace("\r", string.Empty).Split('\n'))
        {
            var line = raw.TrimEnd();

            if (line.StartsWith("@sa ", StringComparison.Ordinal))
            {
                Close();
                day = new SaDay();
                rows = new Dictionary<long, Acc>();
                days[line[4..].Trim()] = day;
                report = string.Empty;
                header = [];
                continue;
            }

            if (line.StartsWith("@o ", StringComparison.Ordinal))
            {
                report = line[3..].Trim();
                header = [];
                hadRows = false;
                continue;
            }

            if (day is null || rows is null || line.Length == 0 || line.StartsWith('@'))
            {
                continue;
            }

            if (line.StartsWith('#'))
            {
                header = line.TrimStart('#', ' ').Split(';');
                continue;
            }

            if (!line.Contains(';'))
            {
                // «Invalid system activity file», «File created by sar/sadc from version …» — этот день не читается
                if (!hadRows && (line.Contains("Invalid", StringComparison.OrdinalIgnoreCase) || line.Contains("version", StringComparison.OrdinalIgnoreCase)))
                {
                    day.Invalid = true;
                }

                continue;
            }

            if (header.Length == 0)
            {
                continue;
            }

            var f = line.Split(';');

            string? Col(string name)
            {
                var i = Array.IndexOf(header, name);
                return i >= 0 && i < f.Length ? f[i] : null;
            }

            double? Num(string name) => Col(name) is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

            // интервал −1 — строка «LINUX-RESTART», не замер
            if (Num("interval") is not { } interval || interval <= 0 || Col("timestamp") is not { } stamp || Time(stamp, context.TzOffset) is not { } end)
            {
                continue;
            }

            hadRows = true;

            if (!rows.TryGetValue(end, out var a))
            {
                rows[end] = a = new Acc();
            }

            a.Interval = Math.Max(a.Interval, (int)interval);

            switch (report)
            {
                case "u" when Col("CPU") is "-1" or "all":
                    if (Num("%idle") is { } idle)
                    {
                        a.Cpu = Math.Clamp(100 - idle - (Num("%iowait") ?? 0), 0, 100);
                        day.Cpu = true;
                    }

                    break;

                case "r":
                    var avail = Num("kbavail")
                        ?? (Num("kbmemfree") is { } free ? free + (Num("kbbuffers") ?? 0) + (Num("kbcached") ?? 0) : null);

                    if (avail is { } kb)
                    {
                        a.AvailKb = kb;
                        day.Mem = true;
                    }

                    break;

                case "n" when Col("IFACE") is { } iface:
                    if (context.Iface.Length > 0 ? iface != context.Iface : iface == "lo")
                    {
                        break;
                    }

                    a.Rx += (Num("rxkB/s") ?? 0) * 1024;
                    a.Tx += (Num("txkB/s") ?? 0) * 1024;
                    a.Net = true;
                    day.Net = true;
                    break;

                case "d" when Col("DEV") is { } dev:
                    var name = DiskName(dev, context.DevNames);

                    if (SkipDisks.Any(p => name.StartsWith(p, StringComparison.Ordinal))
                        || (context.Blocks.Count > 0 && !context.Blocks.Contains(name)))
                    {
                        break;
                    }

                    a.Rd += Num("rkB/s") is { } rk ? rk * 1024 : (Num("rd_sec/s") ?? 0) * 512;
                    a.Wr += Num("wkB/s") is { } wk ? wk * 1024 : (Num("wr_sec/s") ?? 0) * 512;
                    a.Disk = true;
                    day.Disk = true;
                    break;
            }
        }

        Close();
        return days;
    }

    /// <summary>«dev8-0» старого sadf → имя по «8:0» из /sys/block; остальное как есть.</summary>
    private static string DiskName(string dev, IReadOnlyDictionary<string, string> names)
    {
        if (dev.StartsWith("dev", StringComparison.Ordinal) && dev.IndexOf('-') is var dash && dash > 3)
        {
            var key = dev[3..dash] + ":" + dev[(dash + 1)..];

            if (names.TryGetValue(key, out var name))
            {
                return name;
            }
        }

        return dev;
    }

    /// <summary>
    /// «2026-11-20 21:10:01 UTC» → unix-время. Без пояса — тоже UTC (так пишут старые sadf -d); с другим поясом
    /// (sadf -t) — местное время сервера, переводится его нынешним сдвигом.
    /// </summary>
    private static long? Time(string stamp, int tzOffset)
    {
        var s = stamp.Trim();
        var local = false;
        var space = s.LastIndexOf(' ');

        if (space > 10 && !char.IsDigit(s[space + 1]))
        {
            local = !s[(space + 1)..].Equals("UTC", StringComparison.OrdinalIgnoreCase);
            s = s[..space];
        }

        if (!DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
        {
            return null;
        }

        var unix = new DateTimeOffset(at, TimeSpan.Zero).ToUnixTimeSeconds();
        return local ? unix - tzOffset : unix;
    }

    // ── rudog ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Точки rudog из Redis: последние <paramref name="minutes"/> минутных и, если нужно, <paramref name="hours"/> часовых.
    /// Пароль не спрашиваем: Redis с паролем при определении источника не ответит, и rudog не выберется.
    /// </summary>
    public static string RudogCommand(int minutes, int? hours)
    {
        var sb = new StringBuilder();
        sb.Append("echo '@m'; redis-cli --raw LRANGE adm:mon:1m -").Append(minutes.ToString(CultureInfo.InvariantCulture)).Append(" -1 2>/dev/null\n");

        if (hours is { } h)
        {
            sb.Append("echo '@h'; redis-cli --raw LRANGE adm:mon:1h -").Append(h.ToString(CultureInfo.InvariantCulture)).Append(" -1 2>/dev/null\n");
        }

        sb.Append("echo '@n'; date +%s 2>/dev/null\necho '@end'\n");
        return sb.ToString();
    }

    /// <summary>JSON-точки rudog (sysmon.Point): t n c cm m mm s rx rxm tx txm rd rdm wr wrm d — те же, что у своего сборщика.</summary>
    public static (List<MonitorPoint> Minutes, List<MonitorPoint>? Hours, long Now) ParseRudog(string output, bool withHours)
    {
        var minutes = new List<MonitorPoint>();
        var hours = withHours ? new List<MonitorPoint>() : null;
        long now = 0;
        var section = string.Empty;

        foreach (var raw in output.Replace("\r", string.Empty).Split('\n'))
        {
            var line = raw.Trim();

            if (line.StartsWith('@'))
            {
                section = line;
                continue;
            }

            if (line.Length == 0)
            {
                continue;
            }

            if (section == "@n")
            {
                _ = long.TryParse(line, out now);
                continue;
            }

            if (!line.StartsWith('{') || Json(line) is not { } point)
            {
                continue;
            }

            if (section == "@m")
            {
                minutes.Add(point);
            }
            else if (section == "@h")
            {
                hours?.Add(point);
            }
        }

        return (minutes, hours, now);
    }

    private static MonitorPoint? Json(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;

            double D(string k) => r.TryGetProperty(k, out var v) && v.TryGetDouble(out var d) ? d : 0;

            return new MonitorPoint
            {
                T = (long)D("t"),
                N = (int)D("n"),
                Cpu = D("c"),
                CpuMax = D("cm"),
                Mem = (long)D("m"),
                MemMax = (long)D("mm"),
                Swap = (long)D("s"),
                Rx = D("rx"),
                RxMax = D("rxm"),
                Tx = D("tx"),
                TxMax = D("txm"),
                Rd = D("rd"),
                RdMax = D("rdm"),
                Wr = D("wr"),
                WrMax = D("wrm"),
                Disk = (long)D("d"),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
