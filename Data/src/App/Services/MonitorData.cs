using System.Globalization;

namespace MultiServerSync.Services;

/// <summary>Период раздела «Мониторинг»: «Сейчас» — замеры с момента открытия, остальные — история сборщика на сервере.</summary>
public enum MonitorRange
{
    Live,
    Hour,
    Day,
    Week,
    Month,
    Quarter,
}

/// <summary>
/// Окно и шаг каждого периода — как в веб-панели rudog (sysmon.Report): у часа и суток точки минутные,
/// у недели и дальше — часовые; шаг графика крупнее — точки сворачиваются.
/// </summary>
public static class MonitorRanges
{
    public static readonly IReadOnlyList<MonitorRange> All =
        [MonitorRange.Live, MonitorRange.Hour, MonitorRange.Day, MonitorRange.Week, MonitorRange.Month, MonitorRange.Quarter];

    /// <summary>Сколько «Сейчас» держит на графике: последние 5 минут.</summary>
    public const long LiveWindow = 300;

    public static long Window(MonitorRange range) => range switch
    {
        MonitorRange.Hour => 3600,
        MonitorRange.Day => 86400,
        MonitorRange.Week => 7 * 86400,
        MonitorRange.Month => 30 * 86400,
        MonitorRange.Quarter => 90 * 86400,
        _ => LiveWindow,
    };

    /// <summary>
    /// Шаг графика — как у плавной линии в макете «Здоровье сервера»: час — по 2 мин, сутки — по 30 мин, неделя — по 4 ч,
    /// 30 и 90 дней — по суткам (мельче суточный ритм «день/ночь» превращается в пилу).
    /// </summary>
    public static long Step(MonitorRange range) => range switch
    {
        MonitorRange.Hour => 120,
        MonitorRange.Day => 1800,
        MonitorRange.Week => 14400,
        MonitorRange.Month => 86400,
        MonitorRange.Quarter => 86400,
        _ => MonitorProbe.SampleSeconds,
    };

    /// <summary>Неделя и дальше — из часовых точек.</summary>
    public static bool Hourly(MonitorRange range) => range is MonitorRange.Week or MonitorRange.Month or MonitorRange.Quarter;

    public static string Full(MonitorRange range) => range switch
    {
        MonitorRange.Hour => "1 час",
        MonitorRange.Day => "24 часа",
        MonitorRange.Week => "7 дней",
        MonitorRange.Month => "30 дней",
        MonitorRange.Quarter => "90 дней",
        _ => "Сейчас",
    };

    /// <summary>Подпись на узкой странице.</summary>
    public static string Short(MonitorRange range) => range switch
    {
        MonitorRange.Hour => "1 ч",
        MonitorRange.Day => "24 ч",
        MonitorRange.Week => "7 д",
        MonitorRange.Month => "30 д",
        MonitorRange.Quarter => "90 д",
        _ => "Сейчас",
    };

    /// <summary>Ключ в настройках — как в rudog: live, 1h, 24h, 7d, 30d, 90d.</summary>
    public static string Key(MonitorRange range) => range switch
    {
        MonitorRange.Hour => "1h",
        MonitorRange.Day => "24h",
        MonitorRange.Week => "7d",
        MonitorRange.Month => "30d",
        MonitorRange.Quarter => "90d",
        _ => "live",
    };

    public static MonitorRange Parse(string? key) => All.FirstOrDefault(r => Key(r) == key);
}

/// <summary>Мгновенный замер — как Sample в rudog: проценты занятости и скорости в байтах в секунду.</summary>
public sealed class MonitorSample
{
    public long T { get; init; }

    public double Cpu { get; init; }

    public double IOWait { get; init; }

    public double Steal { get; init; }

    public long MemUsed { get; init; }

    public long MemTotal { get; init; }

    public long MemCache { get; init; }

    public long SwapUsed { get; init; }

    public long SwapTotal { get; init; }

    public double Rx { get; init; }

    public double Tx { get; init; }

    public double Rd { get; init; }

    public double Wr { get; init; }

    public long DiskUsed { get; init; }

    public long DiskTotal { get; init; }
}

/// <summary>
/// Свёртка замеров за шаг: среднее и пик. N — сколько 10-секундных замеров внутри: вес при свёртке
/// и объём трафика (скорость · N · 10 с). Тот же Point, что в rudog, без средней нагрузки.
/// </summary>
public sealed class MonitorPoint
{
    public long T { get; set; }

    public int N { get; set; }

    public double Cpu { get; set; }

    public double CpuMax { get; set; }

    public long Mem { get; set; }

    public long MemMax { get; set; }

    public long Swap { get; set; }

    public double Rx { get; set; }

    public double RxMax { get; set; }

    public double Tx { get; set; }

    public double TxMax { get; set; }

    public double Rd { get; set; }

    public double RdMax { get; set; }

    public double Wr { get; set; }

    public double WrMax { get; set; }

    /// <summary>Занято на диске в конце шага.</summary>
    public long Disk { get; set; }

    /// <summary>Замер «Сейчас» точкой из одного замера.</summary>
    public static MonitorPoint Of(MonitorSample s) => new()
    {
        T = s.T,
        N = 1,
        Cpu = s.Cpu,
        CpuMax = s.Cpu,
        Mem = s.MemUsed,
        MemMax = s.MemUsed,
        Swap = s.SwapUsed,
        Rx = s.Rx,
        RxMax = s.Rx,
        Tx = s.Tx,
        TxMax = s.Tx,
        Rd = s.Rd,
        RdMax = s.Rd,
        Wr = s.Wr,
        WrMax = s.Wr,
        Disk = s.DiskUsed,
    };

    /// <summary>
    /// Строка файла сборщика: t n c cm m mm s rx rxm tx txm rd rdm wr wrm d. Неполная или битая — null:
    /// файл мог дописываться в момент чтения.
    /// </summary>
    public static MonitorPoint? Parse(string line)
    {
        var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (f.Length < 16)
        {
            return null;
        }

        var v = new double[16];

        for (var i = 0; i < 16; i++)
        {
            if (!double.TryParse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]))
            {
                return null;
            }
        }

        return new MonitorPoint
        {
            T = (long)v[0],
            N = (int)v[1],
            Cpu = v[2],
            CpuMax = v[3],
            Mem = (long)v[4],
            MemMax = (long)v[5],
            Swap = (long)v[6],
            Rx = v[7],
            RxMax = v[8],
            Tx = v[9],
            TxMax = v[10],
            Rd = v[11],
            RdMax = v[12],
            Wr = v[13],
            WrMax = v[14],
            Disk = (long)v[15],
        };
    }

    /// <summary>foldPoints из rudog: средние — с весом N, пики — максимум, диск — последний.</summary>
    public static MonitorPoint Fold(IReadOnlyList<MonitorPoint> points)
    {
        var p = new MonitorPoint();
        double mem = 0, swap = 0;

        foreach (var q in points)
        {
            double w = q.N;
            p.N += q.N;
            p.Cpu += q.Cpu * w;
            mem += q.Mem * w;
            swap += q.Swap * w;
            p.Rx += q.Rx * w;
            p.Tx += q.Tx * w;
            p.Rd += q.Rd * w;
            p.Wr += q.Wr * w;
            p.CpuMax = Math.Max(p.CpuMax, q.CpuMax);
            p.MemMax = Math.Max(p.MemMax, q.MemMax);
            p.RxMax = Math.Max(p.RxMax, q.RxMax);
            p.TxMax = Math.Max(p.TxMax, q.TxMax);
            p.RdMax = Math.Max(p.RdMax, q.RdMax);
            p.WrMax = Math.Max(p.WrMax, q.WrMax);
            p.Disk = q.Disk;
        }

        if (p.N == 0)
        {
            return p;
        }

        double n = p.N;
        p.Cpu /= n;
        p.Mem = (long)(mem / n);
        p.Swap = (long)(swap / n);
        p.Rx /= n;
        p.Tx /= n;
        p.Rd /= n;
        p.Wr /= n;
        return p;
    }
}

/// <summary>Среднее и пик метрики за период; MaxT — когда был пик.</summary>
public readonly record struct MonitorStat(double Avg, double Max, long MaxT);

/// <summary>Сводка за окно: по метрикам, трафик и изменение занятого места — summarize из rudog.</summary>
public sealed class MonitorPeriod
{
    public long From { get; init; }

    public long To { get; init; }

    public int Samples { get; set; }

    public Dictionary<string, MonitorStat> Stats { get; } = new(StringComparer.Ordinal);

    public double RxBytes { get; set; }

    public double TxBytes { get; set; }

    public long DiskDelta { get; set; }

    public MonitorStat? Of(string key) => Stats.TryGetValue(key, out var stat) ? stat : null;

    public static MonitorPeriod Summarize(IReadOnlyList<MonitorPoint> points, long from, long to)
    {
        var period = new MonitorPeriod { From = from, To = to };
        var sums = new Dictionary<string, (double Sum, double Max, long MaxT)>(StringComparer.Ordinal);

        void Add(string key, double avg, double max, double w, long t)
        {
            var x = sums.GetValueOrDefault(key);
            x.Sum += avg * w;

            if (max > x.Max)
            {
                x.Max = max;
                x.MaxT = t;
            }

            sums[key] = x;
        }

        long firstDisk = 0, lastDisk = 0;

        foreach (var p in points)
        {
            if (p.T < from || p.T >= to || p.N == 0)
            {
                continue;
            }

            double w = p.N;
            period.Samples += p.N;
            Add("cpu", p.Cpu, p.CpuMax, w, p.T);
            Add("mem", p.Mem, p.MemMax, w, p.T);
            Add("rx", p.Rx, p.RxMax, w, p.T);
            Add("tx", p.Tx, p.TxMax, w, p.T);
            Add("rd", p.Rd, p.RdMax, w, p.T);
            Add("wr", p.Wr, p.WrMax, w, p.T);
            period.RxBytes += p.Rx * w * MonitorProbe.SampleSeconds;
            period.TxBytes += p.Tx * w * MonitorProbe.SampleSeconds;

            if (firstDisk == 0)
            {
                firstDisk = p.Disk;
            }

            lastDisk = p.Disk;
        }

        if (period.Samples > 0)
        {
            foreach (var (key, x) in sums)
            {
                period.Stats[key] = new MonitorStat(x.Sum / period.Samples, x.Max, x.MaxT);
            }
        }

        if (firstDisk > 0)
        {
            period.DiskDelta = lastDisk - firstDisk;
        }

        return period;
    }
}

/// <summary>Отчёт для карточек: «сейчас», точки графика, сводка за окно и за такое же окно до него.</summary>
public sealed class MonitorReport
{
    public required MonitorRange Range { get; init; }

    public required long Step { get; init; }

    public required MonitorPeriod Cur { get; init; }

    public required MonitorPeriod Prev { get; init; }

    public required IReadOnlyList<MonitorPoint> Series { get; init; }

    /// <summary>Откуда история; у «Сейчас» — None.</summary>
    public HistorySource Source { get; init; }

    /// <summary>Как часто пишет источник, секунды (у sysstat — его интервал); 0 — не важно.</summary>
    public long SourceStep { get; init; }

    /// <summary>Чего в истории нет: cpu, mem, net, disk, space — такие карточки без истории, а не с нулями.</summary>
    public IReadOnlySet<string> Missing { get; init; } = new HashSet<string>();

    /// <summary>
    /// Report из rudog. <paramref name="points"/> — минутные или часовые точки с незакрытой минутой (часом) в конце,
    /// у «Сейчас» — сами замеры.
    /// </summary>
    /// <param name="sourceStep">Шаг источника: у sysstat точки раз в несколько минут, шаг графика не мельче.</param>
    public static MonitorReport Build(MonitorRange range, IReadOnlyList<MonitorPoint> points, long now,
        HistorySource source = HistorySource.None, long sourceStep = 0, IReadOnlySet<string>? missing = null)
    {
        var win = MonitorRanges.Window(range);
        var step = Math.Max(MonitorRanges.Step(range), sourceStep);

        // у «Сейчас» свежий замер стоит ровно на «сейчас» — окно его включает
        var to = range == MonitorRange.Live ? now + 1 : now;
        var from = to - win;

        return new MonitorReport
        {
            Range = range,
            Step = step,
            Cur = MonitorPeriod.Summarize(points, from, to),
            Prev = MonitorPeriod.Summarize(points, from - win, from),
            Series = Bucket(points.Where(p => p.T >= from).ToList(), step),
            Source = source,
            SourceStep = sourceStep,
            Missing = missing ?? new HashSet<string>(),
        };
    }

    /// <summary>Проредить точки до шага графика, если он крупнее исходного.</summary>
    public static List<MonitorPoint> Bucket(IReadOnlyList<MonitorPoint> points, long step)
    {
        var result = new List<MonitorPoint>(points.Count);
        var bucket = new List<MonitorPoint>();
        long at = -1;

        void Flush()
        {
            if (bucket.Count == 0)
            {
                return;
            }

            var p = bucket.Count > 1 ? MonitorPoint.Fold(bucket) : bucket[0];
            result.Add(new MonitorPoint
            {
                T = at, N = p.N, Cpu = p.Cpu, CpuMax = p.CpuMax, Mem = p.Mem, MemMax = p.MemMax, Swap = p.Swap,
                Rx = p.Rx, RxMax = p.RxMax, Tx = p.Tx, TxMax = p.TxMax, Rd = p.Rd, RdMax = p.RdMax,
                Wr = p.Wr, WrMax = p.WrMax, Disk = p.Disk,
            });
            bucket.Clear();
        }

        foreach (var p in points)
        {
            // сутки и крупнее — от местной полуночи, а не от полуночи UTC
            var off = step >= 86400 ? (long)TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.FromUnixTimeSeconds(p.T)).TotalSeconds : 0;
            var t = (p.T + off) / step * step - off;

            if (t != at)
            {
                Flush();
                at = t;
            }

            bucket.Add(p);
        }

        Flush();
        return result;
    }
}

/// <summary>Числа словами — как в веб-панели rudog: одна-две цифры после точки только у маленьких чисел.</summary>
public static class MonitorFormat
{
    private static readonly string[] Units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];

    public static string Bytes(double n)
    {
        n = Math.Max(0, double.IsFinite(n) ? n : 0);
        var i = 0;

        while (n >= 1024 && i < Units.Length - 1)
        {
            n /= 1024;
            i++;
        }

        var number = i == 0 || n >= 100
            ? Math.Round(n).ToString("0", CultureInfo.InvariantCulture)
            : n >= 10
                ? n.ToString("0.0", CultureInfo.InvariantCulture).Replace(".0", string.Empty)
                : n.ToString("0.##", CultureInfo.InvariantCulture);

        return number + " " + Units[i];
    }

    public static string Rate(double n) => Bytes(n) + "/с";

    public static string Pct(double n) =>
        (n >= 10 || n == 0 ? Math.Round(n).ToString("0", CultureInfo.InvariantCulture) : n.ToString("0.0", CultureInfo.InvariantCulture)) + "%";

    /// <summary>
    /// Изменение к прошлому периоду: у процентов — в п.п., у остального — в %. Меньше порога (0.5 п.п. или 3%) —
    /// «≈ как раньше». Sign: 1 — выросло, −1 — упало, 0 — как раньше или нечего сравнивать.
    /// </summary>
    public static (string Text, int Sign) Delta(double? cur, double? prev, bool pct)
    {
        if (cur is not { } c || prev is not { } p)
        {
            return ("нет данных", 0);
        }

        var d = pct ? c - p : p > 0 ? (c - p) / p * 100 : c > 0 ? 100 : 0;

        if (Math.Abs(d) < (pct ? 0.5 : 3))
        {
            return ("≈ как раньше", 0);
        }

        var v = Math.Abs(d);
        var text = pct
            ? (v >= 10 ? Math.Round(v).ToString("0", CultureInfo.InvariantCulture) : v.ToString("0.0", CultureInfo.InvariantCulture)) + " п.п."
            : (v >= 999 ? ">999" : Math.Round(v).ToString("0", CultureInfo.InvariantCulture)) + "%";

        return d > 0 ? ("▲ +" + text, 1) : ("▼ −" + text, -1);
    }

    /// <summary>Сколько сервер без перезагрузки — как fmtUptime в rudog.</summary>
    public static string Uptime(long sec)
    {
        sec = Math.Max(0, sec);
        long d = sec / 86400, h = sec % 86400 / 3600, m = sec % 3600 / 60;

        return d > 0 ? $"{d} дн {h} ч"
            : h > 0 ? $"{h} ч {m} мин"
            : m > 0 ? $"{m} мин"
            : $"{sec} сек";
    }

    /// <summary>Время точки: у «Сейчас» с секундами, у часа — часы и минуты, у суток и дальше — с датой.</summary>
    public static string Time(long t, MonitorRange range)
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime;

        return range switch
        {
            MonitorRange.Live => at.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            MonitorRange.Hour => at.ToString("HH:mm", CultureInfo.InvariantCulture),
            _ => at.ToString("dd.MM HH:mm", CultureInfo.InvariantCulture),
        };
    }

    public static string Cores(int n) =>
        n <= 0 ? string.Empty : $"{n} {(n % 10 == 1 && n % 100 != 11 ? "ядро" : n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? "ядра" : "ядер")}";
}
