using System.Globalization;
using System.Text;

namespace MultiServerSync.Services;

/// <summary>Постоянное о сервере, что приходит с замером: ядра, интерфейс маршрута, сколько без перезагрузки.</summary>
public sealed record MonitorInfo(int Cores, string Iface, long UptimeSec);

/// <summary>
/// Откуда история: свой сборщик, rudog (Redis), sysstat — или её нет, и почему. Порядок выбора: свой → rudog → sysstat.
/// </summary>
public enum HistorySource
{
    /// <summary>Ничего нет — плашка «Включить сбор» (если есть systemd).</summary>
    None,

    /// <summary>Свой mss-monitor: служба идёт или файл текущей минуты свежий.</summary>
    Own,

    /// <summary>Свой mss-monitor стоит, но не идёт: история из файлов, «Запустить сбор» — снова его.</summary>
    OwnStopped,

    /// <summary>Веб-панель rudog: точки в Redis (adm:mon:1m / adm:mon:1h), читаются без пароля.</summary>
    Rudog,

    /// <summary>sysstat: файлы saDD (или saYYYYMMDD) читаются через sadf.</summary>
    Sysstat,

    /// <summary>sysstat есть, но файлы не читаются этим пользователем — честно говорим, ничего не ставим поверх.</summary>
    SysstatDenied,

    /// <summary>Файлы sysstat есть, а sadf нет (пакет удалили) — читать нечем.</summary>
    SysstatNoSadf,
}

/// <summary>
/// Сбор истории на сервере: systemd, свой сборщик, rudog, sysstat. <paramref name="SaFile"/> — свежий файл sysstat
/// (по нему видна папка), <paramref name="SaKeep"/> — сколько дней хранит sysstat (HISTORY), 0 — неизвестно.
/// </summary>
public sealed record MonitorCollector(bool Systemd, bool Active, bool HasFiles, HistorySource Source, string SaFile, int SaKeep)
{
    /// <summary>Папка файлов sysstat: /var/log/sysstat (Debian, Ubuntu) или /var/log/sa (RHEL и родня).</summary>
    public string SaDir => SaFile.StartsWith("/var/log/sysstat", StringComparison.Ordinal) ? "/var/log/sysstat" : "/var/log/sa";
}

/// <summary>
/// Замер «Сейчас» без установки чего-либо на сервер: одна команда на простом sh читает /proc
/// (процессор, память, маршрут, сеть, диски, время работы) и место на /. Формулы — как в rudog sysmon/proc.go:
/// скорость — разница счётчиков двух замеров, делённая на секунды между ними.
/// </summary>
public static class MonitorProbe
{
    /// <summary>Замер каждые 10 секунд — так же часто пишет и сборщик истории на сервере.</summary>
    public const int SampleSeconds = 10;

    /// <summary>Папка истории сборщика на сервере.</summary>
    public const string DataDir = "/var/lib/mss-monitor";

    /// <summary>
    /// Один снимок счётчиков. /proc/stat — только строки cpu: в строке intr у больших серверов тысячи чисел.
    /// Разделы диска отсекаются по /sys/block, как в rudog: там только целые диски.
    /// </summary>
    private const string Snapshot =
        "echo '@snap'; echo \"@t $(date +%s 2>/dev/null)\"\n"
        + "echo '@cpu'; grep '^cpu' /proc/stat 2>/dev/null\n"
        + "echo '@mem'; cat /proc/meminfo 2>/dev/null\n"
        + "echo '@route'; cat /proc/net/route 2>/dev/null\n"
        + "echo '@net'; cat /proc/net/dev 2>/dev/null\n"
        + "echo '@disk'; cat /proc/diskstats 2>/dev/null\n"
        + "echo '@up'; cat /proc/uptime 2>/dev/null\n";

    /// <summary>
    /// Команда замера. Первый раз — два снимка через секунду: числа видны сразу, а не через 10 секунд.
    /// В конце — есть ли сбор истории: systemd, служба, файл минутных точек.
    /// </summary>
    public static string Command(bool first) =>
        Snapshot
        + (first ? "sleep 1\n" + Snapshot : string.Empty)
        // целые диски и их «major:minor» — старый sadf без -p зовёт диск dev8-0
        + "echo '@blk'; for b in /sys/block/*; do [ -e \"$b\" ] && echo \"${b##*/} $(cat \"$b/dev\" 2>/dev/null)\"; done\n"
        + "echo '@df'; df -Pk / 2>/dev/null\n"
        + "echo '@mon'\n"
        + Detect;

    /// <summary>
    /// Чем на сервере уже собирается история — в той же команде замера, на простом sh (ash, busybox): свой сборщик
    /// (служба или свежий файл текущей минуты), rudog (Redis без пароля), sysstat (sadf и файл за последние двое суток
    /// в /var/log/sysstat или /var/log/sa, в том числе saYYYYMMDD в подпапках YYYYMM; сжатые .gz/.xz не в счёт).
    /// Нет прав на папку или файл — «sadeny». Где нет find, systemctl, redis-cli, sadf — просто пусто.
    /// </summary>
    private const string Detect =
        "[ -d /run/systemd/system ] && command -v systemctl >/dev/null 2>&1 && echo systemd\n"
        + "command -v systemctl >/dev/null 2>&1 && echo \"state $(systemctl is-active mss-monitor 2>/dev/null)\"\n"
        + "[ -s " + DataDir + "/minute.txt ] && echo files\n"
        + "[ -n \"$(find " + DataDir + "/minute.cur -mmin -5 2>/dev/null)\" ] && echo fresh\n"
        + "if command -v redis-cli >/dev/null 2>&1; then n=$(redis-cli --raw LLEN adm:mon:1m 2>/dev/null); "
        + "case \"$n\" in ''|*[!0-9]*|0) ;; *) echo \"rudog $n\";; esac; fi\n"
        + "command -v sadf >/dev/null 2>&1 && echo sadf\n"
        + "for d in /var/log/sysstat /var/log/sa; do [ -d \"$d\" ] || continue; "
        + "if [ ! -r \"$d\" ] || [ ! -x \"$d\" ]; then echo \"sadeny $d\"; continue; fi; "
        + "f=$(find \"$d\" -maxdepth 2 -name 'sa[0-9]*' ! -name '*.*' -mtime -2 2>/dev/null | head -n 1); "
        + "[ -n \"$f\" ] || continue; if [ -r \"$f\" ]; then echo \"safile $f\"; else echo \"sadeny $f\"; fi; done\n"
        + "h=$(sed -n 's/^HISTORY=//p' /etc/sysstat/sysstat /etc/sysconfig/sysstat 2>/dev/null | head -n 1); "
        + "[ -n \"$h\" ] && echo \"sahist $h\"\n"
        + "echo '@end'\n";

    /// <summary>Накопленные счётчики снимка.</summary>
    public sealed class Raw
    {
        public long Time { get; set; }

        public double CpuTotal { get; set; }

        public double CpuIdle { get; set; }

        public double CpuIOWait { get; set; }

        public double CpuSteal { get; set; }

        public int Cores { get; set; }

        public long MemTotal { get; set; }

        public long MemAvail { get; set; }

        public long MemCache { get; set; }

        public long SwapTotal { get; set; }

        public long SwapFree { get; set; }

        public string Iface { get; set; } = string.Empty;

        public double Rx { get; set; }

        public double Tx { get; set; }

        public double RdSect { get; set; }

        public double WrSect { get; set; }

        public bool HasCpu { get; set; }

        /// <summary>Секунды с загрузки из /proc/uptime — с сотыми: по ним точно видно, сколько прошло между снимками.</summary>
        public double Uptime { get; set; }
    }

    /// <summary>Разобранный ответ команды замера.</summary>
    public sealed class Reading
    {
        public List<Raw> Snapshots { get; } = new();

        public long DiskUsed { get; set; }

        public long DiskTotal { get; set; }

        public long UptimeSec { get; set; }

        public required MonitorCollector Collector { get; init; }

        /// <summary>Целые диски из /sys/block.</summary>
        public HashSet<string> Blocks { get; } = new(StringComparer.Ordinal);

        /// <summary>«major:minor» → имя диска: для старых sadf, что пишут dev8-0.</summary>
        public Dictionary<string, string> DevNames { get; } = new(StringComparer.Ordinal);
    }

    public static Reading Parse(string output)
    {
        var sections = new List<(string Name, List<string> Lines)>();

        foreach (var line in output.Replace("\r", string.Empty).Split('\n'))
        {
            if (line.StartsWith('@'))
            {
                sections.Add((line.Trim(), []));
            }
            else if (sections.Count > 0)
            {
                sections[^1].Lines.Add(line);
            }
        }

        var mon = (sections.LastOrDefault(s => s.Name == "@mon").Lines ?? []).Select(l => l.Trim()).ToList();
        var reading = new Reading { Collector = Collector(mon) };

        foreach (var line in sections.LastOrDefault(s => s.Name == "@blk").Lines ?? [])
        {
            var f = Fields(line);

            if (f.Length == 0)
            {
                continue;
            }

            reading.Blocks.Add(f[0]);

            if (f.Length > 1)
            {
                reading.DevNames[f[1]] = f[0];
            }
        }

        var blocks = reading.Blocks;

        Raw? raw = null;

        foreach (var (name, lines) in sections)
        {
            if (name == "@snap")
            {
                raw = new Raw();
                reading.Snapshots.Add(raw);
                continue;
            }

            if (name.StartsWith("@t ", StringComparison.Ordinal) && raw is not null)
            {
                raw.Time = long.TryParse(name[3..].Trim(), out var t) ? t : 0;
                continue;
            }

            switch (name)
            {
                case "@cpu" when raw is not null:
                    ReadCpu(raw, lines);
                    break;
                case "@mem" when raw is not null:
                    ReadMem(raw, lines);
                    break;
                case "@route" when raw is not null:
                    raw.Iface = DefaultIface(lines);
                    break;
                case "@net" when raw is not null:
                    ReadNet(raw, lines);
                    break;
                case "@disk" when raw is not null:
                    ReadDisk(raw, lines, blocks);
                    break;
                case "@up" when raw is not null:
                    raw.Uptime = lines.FirstOrDefault(l => l.Trim().Length > 0) is { } up
                        && double.TryParse(up.Trim().Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var sec)
                            ? sec
                            : 0;
                    reading.UptimeSec = (long)raw.Uptime;
                    break;
                case "@df":
                    ReadDf(reading, lines);
                    break;
            }
        }

        reading.Snapshots.RemoveAll(s => !s.HasCpu);
        return reading;
    }

    /// <summary>
    /// Выбор источника истории: свой → свой остановленный (файлы есть) → rudog → sysstat; sysstat без прав или без
    /// sadf — так и говорим; иначе ничего.
    /// </summary>
    private static MonitorCollector Collector(List<string> mon)
    {
        string Value(string key) => mon.FirstOrDefault(l => l.StartsWith(key + " ", StringComparison.Ordinal))?[(key.Length + 1)..].Trim() ?? string.Empty;

        var systemd = mon.Contains("systemd");
        var active = mon.Contains("state active");
        var files = mon.Contains("files");
        var saFile = Value("safile");
        var keep = int.TryParse(Value("sahist"), out var days) ? days : 0;

        var source = active || mon.Contains("fresh") ? HistorySource.Own
            : files ? HistorySource.OwnStopped
            : Value("rudog").Length > 0 ? HistorySource.Rudog
            : saFile.Length > 0 && mon.Contains("sadf") ? HistorySource.Sysstat
            : Value("sadeny").Length > 0 ? HistorySource.SysstatDenied
            : saFile.Length > 0 ? HistorySource.SysstatNoSadf
            : HistorySource.None;

        return new MonitorCollector(systemd, active, files, source, saFile, keep);
    }

    private static double Num(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static string[] Fields(string line) => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>user nice system idle iowait irq softirq steal — guest уже внутри user.</summary>
    private static void ReadCpu(Raw raw, List<string> lines)
    {
        foreach (var line in lines)
        {
            var f = Fields(line);

            if (f.Length == 0)
            {
                continue;
            }

            if (f[0] == "cpu" && f.Length >= 5)
            {
                double total = 0;

                for (var i = 1; i <= 8 && i < f.Length; i++)
                {
                    total += Num(f[i]);
                }

                raw.CpuTotal = total;
                raw.CpuIdle = Num(f[4]);
                raw.CpuIOWait = Num(f[5]);
                raw.CpuSteal = f.Length > 8 ? Num(f[8]) : 0;
                raw.HasCpu = true;
            }
            else if (f[0].Length > 3 && f[0].StartsWith("cpu", StringComparison.Ordinal) && char.IsDigit(f[0][3]))
            {
                raw.Cores++;
            }
        }
    }

    private static void ReadMem(Raw raw, List<string> lines)
    {
        long free = 0, buffers = 0, cached = 0, reclaim = 0;
        long? avail = null;

        foreach (var line in lines)
        {
            var f = Fields(line);

            if (f.Length < 2)
            {
                continue;
            }

            var v = (long)Num(f[1]) * 1024;

            switch (f[0])
            {
                case "MemTotal:": raw.MemTotal = v; break;
                case "MemAvailable:": avail = v; break;
                case "MemFree:": free = v; break;
                case "Buffers:": buffers = v; break;
                case "Cached:": cached = v; break;
                case "SReclaimable:": reclaim = v; break;
                case "SwapTotal:": raw.SwapTotal = v; break;
                case "SwapFree:": raw.SwapFree = v; break;
            }
        }

        // старые ядра без MemAvailable: свободно плюс буферы и кэш — так же считает сборщик
        raw.MemAvail = avail ?? free + buffers + cached;
        raw.MemCache = buffers + cached + reclaim;
    }

    /// <summary>Интерфейс маршрута по умолчанию (eth0, ens3…): его трафик и есть трафик сервера; туннели и lo не считаем.</summary>
    private static string DefaultIface(List<string> lines)
    {
        foreach (var line in lines)
        {
            var f = Fields(line);

            if (f.Length >= 2 && f[1] == "00000000")
            {
                return f[0];
            }
        }

        return string.Empty;
    }

    private static void ReadNet(Raw raw, List<string> lines)
    {
        foreach (var line in lines)
        {
            var colon = line.IndexOf(':');

            if (colon < 0)
            {
                continue;
            }

            var name = line[..colon].Trim();

            if ((raw.Iface.Length > 0 && name != raw.Iface) || (raw.Iface.Length == 0 && name == "lo"))
            {
                continue;
            }

            var f = Fields(line[(colon + 1)..]);

            if (f.Length < 9)
            {
                continue;
            }

            raw.Rx += Num(f[0]);
            raw.Tx += Num(f[8]);
        }
    }

    private static readonly string[] SkipDisks = ["loop", "ram", "zram", "dm-", "sr", "fd", "md"];

    /// <summary>Секторы чтения и записи целых дисков: без разделов, loop, zram и dm-* — иначе байты считались бы дважды.</summary>
    private static void ReadDisk(Raw raw, List<string> lines, HashSet<string> blocks)
    {
        foreach (var line in lines)
        {
            var f = Fields(line);

            if (f.Length < 10)
            {
                continue;
            }

            var name = f[2];

            if (SkipDisks.Any(p => name.StartsWith(p, StringComparison.Ordinal)) || !blocks.Contains(name))
            {
                continue;
            }

            raw.RdSect += Num(f[5]);
            raw.WrSect += Num(f[9]);
        }
    }

    /// <summary>Занято и всего на корневой файловой системе, как df: всего минус свободно (запас root — занятое).</summary>
    private static void ReadDf(Reading reading, List<string> lines)
    {
        foreach (var line in lines)
        {
            var f = Fields(line);

            if (f.Length >= 4 && long.TryParse(f[1], out var total) && long.TryParse(f[2], out var used))
            {
                reading.DiskTotal = total * 1024;
                reading.DiskUsed = used * 1024;
            }
        }
    }

    private static double Sub(double b, double a) => b < a ? 0 : b - a;

    private static double Clamp(double v) => Math.Min(100, Math.Max(0, v));

    /// <summary>makeSample из rudog: замер из двух снимков. <paramref name="seconds"/> — сколько прошло между ними.</summary>
    public static MonitorSample Sample(Raw a, Raw b, double seconds, long t, long diskUsed, long diskTotal)
    {
        double cpu = 0, iow = 0, steal = 0, rx = 0, tx = 0, rd = 0, wr = 0;
        var dt = Sub(b.CpuTotal, a.CpuTotal);

        if (dt > 0)
        {
            var idle = Sub(b.CpuIdle, a.CpuIdle);
            var wait = Sub(b.CpuIOWait, a.CpuIOWait);
            cpu = Clamp(100 * (dt - idle - wait) / dt);
            iow = Clamp(100 * wait / dt);
            steal = Clamp(100 * Sub(b.CpuSteal, a.CpuSteal) / dt);
        }

        if (seconds > 0)
        {
            // сменился интерфейс маршрута по умолчанию — счётчики несравнимы
            if (a.Iface == b.Iface)
            {
                rx = Sub(b.Rx, a.Rx) / seconds;
                tx = Sub(b.Tx, a.Tx) / seconds;
            }

            rd = Sub(b.RdSect, a.RdSect) * 512 / seconds;
            wr = Sub(b.WrSect, a.WrSect) * 512 / seconds;
        }

        return new MonitorSample
        {
            T = t,
            Cpu = cpu,
            IOWait = iow,
            Steal = steal,
            MemTotal = b.MemTotal,
            MemUsed = Math.Max(0, b.MemTotal - b.MemAvail),
            MemCache = b.MemCache,
            SwapTotal = b.SwapTotal,
            SwapUsed = Math.Max(0, b.SwapTotal - b.SwapFree),
            Rx = rx,
            Tx = tx,
            Rd = rd,
            Wr = wr,
            DiskUsed = diskUsed,
            DiskTotal = diskTotal,
        };
    }

    /// <summary>
    /// Чтение истории: новые строки минутных точек (после последней известной), незакрытая минута и, если нужно,
    /// новые часовые точки. Первый раз — файлы целиком: их держит небольшими сам сборщик.
    /// </summary>
    public static string HistoryCommand(long afterMinute, long? afterHour)
    {
        var sb = new StringBuilder();
        sb.Append("d=").Append(DataDir).Append('\n');
        sb.Append("echo '@m'; awk -v t=").Append(afterMinute.ToString(CultureInfo.InvariantCulture)).Append(" '$1>t' \"$d/minute.txt\" 2>/dev/null\n");
        sb.Append("echo '@c'; cat \"$d/minute.cur\" 2>/dev/null\n");

        if (afterHour is { } hour)
        {
            sb.Append("echo '@h'; awk -v t=").Append(hour.ToString(CultureInfo.InvariantCulture)).Append(" '$1>t' \"$d/hour.txt\" 2>/dev/null\n");
        }

        sb.Append("echo '@n'; date +%s 2>/dev/null\n");
        sb.Append("echo '@end'\n");
        return sb.ToString();
    }

    /// <summary>Ответ <see cref="HistoryCommand"/>: точки по разделам @m, @c, @h и время сервера.</summary>
    public static (List<MonitorPoint> Minutes, MonitorPoint? Current, List<MonitorPoint>? Hours, long Now) ParseHistory(string output, bool withHours)
    {
        var minutes = new List<MonitorPoint>();
        var hours = withHours ? new List<MonitorPoint>() : null;
        MonitorPoint? current = null;
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

            switch (section)
            {
                case "@m" when MonitorPoint.Parse(line) is { } m:
                    minutes.Add(m);
                    break;
                case "@c" when MonitorPoint.Parse(line) is { } c:
                    current = c;
                    break;
                case "@h" when hours is not null && MonitorPoint.Parse(line) is { } h:
                    hours.Add(h);
                    break;
                case "@n":
                    _ = long.TryParse(line, out now);
                    break;
            }
        }

        return (minutes, current, hours, now);
    }
}
