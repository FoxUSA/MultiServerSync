using System.IO;
using System.Text.Json;

namespace MultiServerSync.Services;

/// <summary>
/// История «Мониторинга» на диске: прочитанное с сервера переживает перезапуск, следующий запуск показывает его сразу
/// и дочитывает с сервера только новое (у sysstat — только изменившиеся дни). Лежит в UserData, как всё у программы:
/// во внешние папки она ничего не пишет. Не прочитался — просто читаем с сервера, как без него.
/// </summary>
public sealed partial class MonitorFeed
{
    private static readonly string CacheDir = Path.Combine(Core.AppPaths.UserDataDir, "monitor-cache");

    /// <summary>Чаще не пишем: файл пересобирается целиком.</summary>
    private static readonly TimeSpan CacheEvery = TimeSpan.FromMinutes(2);

    private const int CacheVersion = 1;

    private bool _cacheLoaded;
    private bool _cacheDirty;
    private DateTime _cacheSavedAt = DateTime.MinValue;

    private string CachePath => Path.Combine(CacheDir, $"{Server.Id}.json");

    internal sealed class CacheFile
    {
        public int V { get; set; }

        public HistorySource Source { get; set; }

        public List<double[]> Minutes { get; set; } = new();

        public List<double[]> Hours { get; set; } = new();

        public List<CacheDay> Days { get; set; } = new();
    }

    internal sealed class CacheDay
    {
        public string Path { get; set; } = string.Empty;

        public string Sig { get; set; } = string.Empty;

        public bool Cpu { get; set; }

        public bool Mem { get; set; }

        public bool Net { get; set; }

        public bool Disk { get; set; }

        public bool Invalid { get; set; }

        public int Interval { get; set; }

        public List<double[]> Points { get; set; } = new();
    }

    /// <summary>Точка — строкой чисел: в разы меньше файла с именами полей.</summary>
    private static double[] Pack(MonitorPoint p) =>
    [
        p.T, p.N, Math.Round(p.Cpu, 2), Math.Round(p.CpuMax, 2), p.Mem, p.MemMax, p.Swap,
        Math.Round(p.Rx, 1), Math.Round(p.RxMax, 1), Math.Round(p.Tx, 1), Math.Round(p.TxMax, 1),
        Math.Round(p.Rd, 1), Math.Round(p.RdMax, 1), Math.Round(p.Wr, 1), Math.Round(p.WrMax, 1), p.Disk,
    ];

    private static MonitorPoint? Unpack(double[] a) => a.Length < 16 ? null : new MonitorPoint
    {
        T = (long)a[0], N = (int)a[1], Cpu = a[2], CpuMax = a[3], Mem = (long)a[4], MemMax = (long)a[5], Swap = (long)a[6],
        Rx = a[7], RxMax = a[8], Tx = a[9], TxMax = a[10], Rd = a[11], RdMax = a[12], Wr = a[13], WrMax = a[14], Disk = (long)a[15],
    };

    private static List<MonitorPoint> UnpackAll(IEnumerable<double[]> rows) => rows.Select(Unpack).OfType<MonitorPoint>().ToList();

    /// <summary>Прочитанное в прошлые запуски — в память, до первого обращения к серверу. Один раз за жизнь источника.</summary>
    private void LoadCache()
    {
        if (_cacheLoaded)
        {
            return;
        }

        _cacheLoaded = true;

        try
        {
            if (!File.Exists(CachePath))
            {
                return;
            }

            var file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(CachePath));

            if (file is null || file.V != CacheVersion || file.Source == HistorySource.None)
            {
                return;
            }

            lock (_gate)
            {
                // с сервера уже что-то прочитано — оно новее
                if (_loadedSource != HistorySource.None)
                {
                    return;
                }

                _loadedSource = file.Source;

                if (file.Source == HistorySource.Sysstat)
                {
                    foreach (var cached in file.Days)
                    {
                        var day = new MonitorHistory.SaDay
                        {
                            Cpu = cached.Cpu, Mem = cached.Mem, Net = cached.Net, Disk = cached.Disk,
                            Invalid = cached.Invalid, Interval = cached.Interval,
                        };

                        day.Points.AddRange(UnpackAll(cached.Points));
                        _saDays[cached.Path] = (cached.Sig, day);
                    }

                    RebuildSysstat();
                    _saLoaded = _saDays.Count > 0;
                }
                else
                {
                    _minutes.AddRange(UnpackAll(file.Minutes));
                    _hours.AddRange(UnpackAll(file.Hours));

                    // загружено — дочитывается только хвост после последней точки, и сразу
                    _minutesLoaded = _minutes.Count > 0;
                    _hoursLoaded = _hours.Count > 0;
                    _minutesAt = DateTime.MinValue;
                    _hoursAt = DateTime.MinValue;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug($"Мониторинг: кэш истории не прочитан — {ex.Message}", Server.Display);
        }
    }

    /// <summary>Прочитанное с сервера — на диск; не чаще раза в пару минут, при выходе — сразу.</summary>
    private void SaveCache(bool force = false)
    {
        CacheFile file;

        lock (_gate)
        {
            if (!_cacheDirty || _loadedSource == HistorySource.None || (!force && DateTime.UtcNow - _cacheSavedAt < CacheEvery))
            {
                return;
            }

            _cacheDirty = false;
            _cacheSavedAt = DateTime.UtcNow;

            file = new CacheFile
            {
                V = CacheVersion,
                Source = _loadedSource,
                Minutes = _minutes.Select(Pack).ToList(),
                Hours = _hours.Select(Pack).ToList(),
                Days = _saDays.Select(d => new CacheDay
                {
                    Path = d.Key,
                    Sig = d.Value.Sig,
                    Cpu = d.Value.Day.Cpu,
                    Mem = d.Value.Day.Mem,
                    Net = d.Value.Day.Net,
                    Disk = d.Value.Day.Disk,
                    Invalid = d.Value.Day.Invalid,
                    Interval = d.Value.Day.Interval,
                    Points = d.Value.Day.Points.Select(Pack).ToList(),
                }).ToList(),
            };
        }

        try
        {
            Directory.CreateDirectory(CacheDir);
            var tmp = CachePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file));
            File.Move(tmp, CachePath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Debug($"Мониторинг: кэш истории не записан — {ex.Message}", Server.Display);
        }
    }

    /// <summary>Выход из программы: недописанное на диск.</summary>
    public static void SaveAllCaches()
    {
        List<MonitorFeed> feeds;

        lock (Feeds)
        {
            feeds = Feeds.Values.ToList();
        }

        foreach (var feed in feeds)
        {
            feed.SaveCache(force: true);
        }
    }
}
