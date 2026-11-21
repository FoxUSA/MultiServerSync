using System.Diagnostics;
using System.Windows;
using MultiServerSync.Models;

namespace MultiServerSync.Services;

/// <summary>
/// Замеры одного сервера для раздела «Мониторинг» — один источник на сервер, сколько бы окон его ни показывало:
/// главное окно и вынесенные подписываются на него и отписываются. С запуска программы на каждый сервер подписан
/// и фоновый зритель (<see cref="MonitorWarmup"/>): «Сейчас» копится и история читается заранее, а раздел
/// открывается сразу с данными.
/// «Сейчас» — замер каждые 10 секунд по SSH, без установки чего-либо на сервер. История — файлы сборщика
/// на сервере, читаются хвостом (только новые строки) и делятся между всеми окнами этого сервера.
/// Связь пропала — в карточках «нет связи», повтор на следующем замере; в журнал — одна запись о потере
/// и одна о возврате, а не каждые 10 секунд.
/// </summary>
public sealed partial class MonitorFeed
{
    private static readonly Dictionary<string, MonitorFeed> Feeds = new(StringComparer.Ordinal);

    /// <summary>Фоновый зритель: читает заранее, но на экране его нет — о потере связи в журнал не пишет.</summary>
    public static object Background { get; } = new();

    /// <summary>Без окна на экране сервер, что не отвечает, опрашивается реже.</summary>
    private static readonly TimeSpan BackgroundRetry = TimeSpan.FromSeconds(60);

    /// <summary>Сколько держится один замер: дольше — сервер считается не ответившим.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(25);

    /// <summary>«Сейчас» помнит 10 минут: 5 на графике и 5 «раньше» для сравнения.</summary>
    private const long LiveKeep = 2 * MonitorRanges.LiveWindow;

    /// <summary>Сколько хранит сборщик: минутные — 48 часов, часовые — 180 дней. Старше в памяти не держим.</summary>
    private const long MinuteKeep = 48 * 3600;
    private const long HourKeep = 180 * 86400;

    private readonly Lock _gate = new();
    private readonly Dictionary<object, MonitorRange> _viewers = new();
    private readonly List<MonitorPoint> _live = new();
    private readonly List<MonitorPoint> _minutes = new();
    private readonly List<MonitorPoint> _hours = new();

    private MonitorPoint? _currentMinute;
    private bool _minutesLoaded;
    private bool _hoursLoaded;
    private DateTime _minutesAt;
    private DateTime _hoursAt;

    private CancellationTokenSource? _run;
    private CancellationTokenSource _wake = new();
    private RemoteSession? _session;
    private MonitorProbe.Raw? _lastRaw;

    /// <summary>Связь пропала и об этом уже есть запись в журнале — следующая будет о возврате.</summary>
    private bool _lossLogged;

    private long _serverNow;
    private long _serverNowStamp;

    private MonitorFeed(ServerEntry server)
    {
        Server = server;
    }

    /// <summary>Источник замеров сервера: один на сервер на всё время работы программы.</summary>
    public static MonitorFeed For(ServerEntry server)
    {
        lock (Feeds)
        {
            if (!Feeds.TryGetValue(server.Id, out var feed) || !ReferenceEquals(feed.Server, server))
            {
                feed = new MonitorFeed(server);
                Feeds[server.Id] = feed;
            }

            return feed;
        }
    }

    public ServerEntry Server { get; }

    /// <summary>Свежие данные или смена состояния — окна перерисовывают карточки. Приходит в потоке окна.</summary>
    public event Action? Changed;

    /// <summary>Последний замер; null — ещё ни одного.</summary>
    public MonitorSample? Now { get; private set; }

    public MonitorInfo? Info { get; private set; }

    /// <summary>Когда пришёл последний удачный замер (время этого компьютера).</summary>
    public DateTime? UpdatedAt { get; private set; }

    /// <summary>Сбор истории на сервере; null — ещё не знаем (первый замер не пришёл).</summary>
    public MonitorCollector? Collector { get; private set; }

    /// <summary>Почему нет связи; null — связь есть или ещё не пробовали.</summary>
    public string? Trouble { get; private set; }

    /// <summary>Идёт «Включить сбор».</summary>
    public bool Installing { get; private set; }

    /// <summary>Опрос идёт — кто-то показывает этот сервер.</summary>
    public bool IsWatched
    {
        get
        {
            lock (_gate)
            {
                return _viewers.Count > 0;
            }
        }
    }

    /// <summary>Окно начало показывать сервер с этим периодом. Первое — запускает опрос.</summary>
    public void Watch(object viewer, MonitorRange range)
    {
        bool start;

        lock (_gate)
        {
            _viewers[viewer] = range;
            start = _run is null;

            if (start)
            {
                _run = new CancellationTokenSource();
            }
        }

        if (start)
        {
            var token = _run!.Token;
            _ = Task.Run(() => RunAsync(token));
        }
        else
        {
            Kick();
        }
    }

    /// <summary>Окно сменило период: история нужного вида читается сразу, не дожидаясь следующего замера.</summary>
    public void SetRange(object viewer, MonitorRange range)
    {
        lock (_gate)
        {
            if (!_viewers.ContainsKey(viewer) || _viewers[viewer] == range)
            {
                return;
            }

            _viewers[viewer] = range;
        }

        Kick();
    }

    /// <summary>Окно перестало показывать сервер. Последнее — останавливает опрос и забывает «Сейчас».</summary>
    public void Unwatch(object viewer)
    {
        CancellationTokenSource? stop = null;

        lock (_gate)
        {
            if (!_viewers.Remove(viewer) || _viewers.Count > 0)
            {
                return;
            }

            stop = _run;
            _run = null;
            _live.Clear();
            _lastRaw = null;
            Now = null;
            UpdatedAt = null;
            Trouble = null;
        }

        // без Dispose: токен ещё держат связанные ожидания цикла — отмена их и разбудит
        stop?.Cancel();
    }

    /// <summary>Разбудить опрос: после установки сборщика, смены периода, нового окна.</summary>
    public void Kick()
    {
        CancellationTokenSource old;

        lock (_gate)
        {
            old = _wake;
            _wake = new CancellationTokenSource();
        }

        old.Cancel();
    }

    /// <summary>«Включить сбор» начался или кончился — плашка над карточками меняет вид.</summary>
    public void SetInstalling(bool on)
    {
        Installing = on;
        Notify();

        if (!on)
        {
            // сразу перечитать, есть ли теперь служба, и забрать первые точки истории
            _minutesAt = DateTime.MinValue;
            Kick();
        }
    }

    /// <summary>Сервер показывает окно, а не только фоновое чтение.</summary>
    private bool OnScreen()
    {
        lock (_gate)
        {
            return _viewers.Keys.Any(v => !ReferenceEquals(v, Background));
        }
    }

    private bool HistoryWanted(out bool hourly)
    {
        lock (_gate)
        {
            hourly = _viewers.Values.Any(MonitorRanges.Hourly);
            return _viewers.Values.Any(r => r != MonitorRange.Live);
        }
    }

    private async Task RunAsync(CancellationToken stop)
    {
        var first = true;

        // прочитанное в прошлые запуски — на экран до первого ответа сервера
        LoadCache();
        Notify();

        while (!stop.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp();

            if (await ProbeAsync(first, stop))
            {
                first = false;

                try
                {
                    await HistoryAsync(stop);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested)
                {
                    // история не прочиталась — покажется прочитанное раньше, повтор на следующем замере
                    AppLog.Debug($"Мониторинг: история не прочиталась — {ex.Message}", Server.Display);
                }

                SaveCache();
            }
            else
            {
                // без прошлого снимка скорость не посчитать: следующий замер снова из двух снимков
                first = true;
            }

            if (stop.IsCancellationRequested)
            {
                break;
            }

            Notify();

            // не отвечает, а смотрит только фон — реже; окно откроют — разбудит сразу
            var every = Trouble is not null && !OnScreen() ? BackgroundRetry : TimeSpan.FromSeconds(MonitorProbe.SampleSeconds);
            var left = every - Stopwatch.GetElapsedTime(started);

            CancellationToken wake;

            lock (_gate)
            {
                wake = _wake.Token;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, wake);

            try
            {
                await Task.Delay(left > TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1), linked.Token);
            }
            catch (OperationCanceledException)
            {
                // разбудили: опрос продолжается сразу (кроме остановки — её ловит условие цикла)
                if (!stop.IsCancellationRequested && wake.IsCancellationRequested)
                {
                    // будили не ради свежего замера, а ради истории или новой подписки — замер не повторяем
                    try
                    {
                        await HistoryAsync(stop);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested)
                    {
                        AppLog.Debug($"Мониторинг: история не прочиталась — {ex.Message}", Server.Display);
                    }

                    SaveCache();
                    Notify();

                    // до следующего замера — оставшееся время
                    var rest = TimeSpan.FromSeconds(MonitorProbe.SampleSeconds) - Stopwatch.GetElapsedTime(started);

                    if (rest > TimeSpan.Zero)
                    {
                        try
                        {
                            await Task.Delay(rest, stop);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                }
            }
        }

        lock (_gate)
        {
            // подписались снова, пока этот цикл выходил, — у них свой цикл, сессию держит он
            if (_run is not null)
            {
                return;
            }
        }

        ReleaseSession();
    }

    private RemoteSession Session()
    {
        lock (_gate)
        {
            return _session ??= RemoteSession.Acquire(Server, AppState.Current.Settings);
        }
    }

    private void ReleaseSession()
    {
        RemoteSession? session;

        lock (_gate)
        {
            session = _session;
            _session = null;
        }

        session?.Release();
    }

    private async Task<(string Output, string Error)> ExecAsync(string script, CancellationToken stop)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(stop);
        limit.CancelAfter(ProbeTimeout);

        try
        {
            return await Session().ExecAsync(script, limit.Token);
        }
        catch (ObjectDisposedException)
        {
            // сервер правили — старое подключение выброшено, следующий раз возьмётся новое
            ReleaseSession();
            throw;
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            throw new TimeoutException($"сервер не ответил за {ProbeTimeout.TotalSeconds:0} с");
        }
    }

    /// <returns>Замер пришёл.</returns>
    private async Task<bool> ProbeAsync(bool first, CancellationToken stop)
    {
        string output;

        try
        {
            (output, _) = await ExecAsync(MonitorProbe.Command(first), stop);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            Lost(ex is TimeoutException ? ex.Message : SshService.Describe(ex, Server));
            return false;
        }

        var reading = MonitorProbe.Parse(output);

        if (reading.Snapshots.Count == 0)
        {
            Lost("сервер не отдал /proc — замер на нём невозможен");
            return false;
        }

        var last = reading.Snapshots[^1];
        var before = reading.Snapshots.Count > 1 ? reading.Snapshots[^2] : _lastRaw;
        var t = last.Time > 0 ? last.Time : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        lock (_gate)
        {
            if (stop.IsCancellationRequested)
            {
                return false;
            }

            // секунды между снимками — по /proc/uptime: он с сотыми и не зависит от часов сервера.
            // Меньше нуля — сервер перезагрузился, счётчики начались заново: этот снимок только база
            if (before is not null && last.Uptime - before.Uptime is var seconds && seconds > 0)
            {
                var sample = MonitorProbe.Sample(before, last, seconds, t, reading.DiskUsed, reading.DiskTotal);
                Now = sample;
                _live.Add(MonitorPoint.Of(sample));
                _live.RemoveAll(p => p.T <= t - LiveKeep);
            }

            _lastRaw = last;
            _blocks = reading.Blocks;
            _devNames = reading.DevNames;
            Info = new MonitorInfo(last.Cores, last.Iface, reading.UptimeSec);
            Collector = reading.Collector;
            UpdatedAt = DateTime.Now;
            _serverNow = t;
            _serverNowStamp = Stopwatch.GetTimestamp();
        }

        Back();
        return true;
    }

    /// <summary>Нет связи: в журнал — только первый раз, дальше молча до возврата.</summary>
    private void Lost(string why)
    {
        Trouble = why;

        // фоновое чтение журнал не засоряет: сервер, который никто не смотрит, может и не отвечать
        if (_lossLogged || !OnScreen())
        {
            return;
        }

        _lossLogged = true;
        Dispatch(() => ActionLog.Warn($"Мониторинг: нет связи с сервером — {why}", Server, AppSection.Monitor, AppState.Current.MonitorTabOf(Server),
            ["замеры повторяются каждые 10 с, о возврате связи будет запись"]));
    }

    private void Back()
    {
        var was = Trouble;
        Trouble = null;

        if (!_lossLogged)
        {
            return;
        }

        _lossLogged = false;
        Dispatch(() => ActionLog.Info("Мониторинг: связь с сервером восстановлена", Server, AppSection.Monitor, AppState.Current.MonitorTabOf(Server),
            was is null ? null : [$"до этого: {was}"]));
    }

    /// <summary>Из какого источника прочитано то, что лежит в памяти: источник сменился — читать заново.</summary>
    private HistorySource _loadedSource = HistorySource.None;

    /// <summary>sysstat: прочитанные дни (путь → подпись файла и точки) и сведённое из них.</summary>
    private readonly Dictionary<string, (string Sig, MonitorHistory.SaDay Day)> _saDays = new(StringComparer.Ordinal);
    private List<MonitorPoint> _sa = new();
    private bool _saLoaded;
    private DateTime _saAt;
    private long _saWindow;
    private int _saInterval;
    private bool _saCpu;
    private bool _saMem;
    private bool _saNet;
    private bool _saDisk;

    /// <summary>Что отдал последний замер «Сейчас»: диски и их номера — для чужих источников истории.</summary>
    private HashSet<string> _blocks = new(StringComparer.Ordinal);
    private Dictionary<string, string> _devNames = new(StringComparer.Ordinal);

    /// <summary>Источник истории, из которого читаем: остановленный свой — те же файлы.</summary>
    private static HistorySource ReadFrom(HistorySource source) => source == HistorySource.OwnStopped ? HistorySource.Own : source;

    /// <summary>
    /// История: свой сборщик и rudog — минутные точки раз в минуту, только новые; часовые — раз в 10 минут, когда
    /// выбрана неделя и дальше. sysstat — список файлов дней раз в минуту, перечитываются только новые и изменённые,
    /// не больше нескольких файлов за заход. Источника нет — читать нечего.
    /// </summary>
    private async Task HistoryAsync(CancellationToken stop)
    {
        if (!HistoryWanted(out var hourly) || Collector is not { } collector)
        {
            return;
        }

        var source = ReadFrom(collector.Source);

        if (source != _loadedSource)
        {
            lock (_gate)
            {
                _minutes.Clear();
                _hours.Clear();
                _currentMinute = null;
                _minutesLoaded = false;
                _hoursLoaded = false;
                _saDays.Clear();
                _sa = new List<MonitorPoint>();
                _saLoaded = false;
                _loadedSource = source;
            }
        }

        switch (source)
        {
            case HistorySource.Own:
                await OwnHistoryAsync(hourly, stop);
                break;
            case HistorySource.Rudog:
                await RudogHistoryAsync(hourly, stop);
                break;
            case HistorySource.Sysstat:
                await SysstatHistoryAsync(collector, stop);
                break;
        }
    }

    /// <summary>Свой сборщик: новые строки минутных и часовых файлов.</summary>
    private async Task OwnHistoryAsync(bool hourly, CancellationToken stop)
    {
        var now = DateTime.UtcNow;
        var minutesDue = !_minutesLoaded || now - _minutesAt > TimeSpan.FromSeconds(55);
        var hoursDue = hourly && (!_hoursLoaded || now - _hoursAt > TimeSpan.FromMinutes(10));

        if (!minutesDue && !hoursDue)
        {
            return;
        }

        long afterMinute, afterHour;

        lock (_gate)
        {
            afterMinute = _minutesLoaded && _minutes.Count > 0 ? _minutes[^1].T : -1;
            afterHour = _hoursLoaded && _hours.Count > 0 ? _hours[^1].T : -1;
        }

        var (output, _) = await ExecAsync(MonitorProbe.HistoryCommand(afterMinute, hourly ? afterHour : null), stop);
        var (minutes, current, hours, serverNow) = MonitorProbe.ParseHistory(output, hourly);

        lock (_gate)
        {
            var at = serverNow > 0 ? serverNow : _serverNow;

            if (!_minutesLoaded)
            {
                _minutes.Clear();
            }

            _minutes.AddRange(minutes.Where(p => _minutes.Count == 0 || p.T > _minutes[^1].T));
            _minutes.RemoveAll(p => p.T < at - MinuteKeep);
            _currentMinute = current;
            _minutesLoaded = true;
            _minutesAt = now;
            _cacheDirty = true;

            if (hours is not null)
            {
                if (!_hoursLoaded)
                {
                    _hours.Clear();
                }

                _hours.AddRange(hours.Where(p => _hours.Count == 0 || p.T > _hours[^1].T));
                _hours.RemoveAll(p => p.T < at - HourKeep);
                _hoursLoaded = true;
                _hoursAt = now;
            }
        }
    }

    /// <summary>
    /// rudog: первый раз — списки Redis целиком, дальше — только хвост, сколько минут (часов) прошло с последней точки.
    /// Незакрытой минуты rudog не отдаёт — график кончается на прошлой минуте.
    /// </summary>
    private async Task RudogHistoryAsync(bool hourly, CancellationToken stop)
    {
        var now = DateTime.UtcNow;
        var minutesDue = !_minutesLoaded || now - _minutesAt > TimeSpan.FromSeconds(55);
        var hoursDue = hourly && (!_hoursLoaded || now - _hoursAt > TimeSpan.FromMinutes(10));

        if (!minutesDue && !hoursDue)
        {
            return;
        }

        int minutes, hours;

        lock (_gate)
        {
            var at = ServerNow();
            minutes = _minutesLoaded && _minutes.Count > 0 ? (int)Math.Clamp((at - _minutes[^1].T) / 60 + 2, 2, 2880) : 2880;
            hours = _hoursLoaded && _hours.Count > 0 ? (int)Math.Clamp((at - _hours[^1].T) / 3600 + 2, 2, 4320) : 4320;
        }

        var (output, _) = await ExecAsync(MonitorHistory.RudogCommand(minutes, hourly ? hours : null), stop);
        var (got, gotHours, serverNow) = MonitorHistory.ParseRudog(output, hourly);

        lock (_gate)
        {
            var at = serverNow > 0 ? serverNow : _serverNow;

            _minutes.AddRange(got.Where(p => _minutes.Count == 0 || p.T > _minutes[^1].T));
            _minutes.RemoveAll(p => p.T < at - MinuteKeep);
            _minutesLoaded = true;
            _minutesAt = now;
            _cacheDirty = true;

            if (gotHours is not null)
            {
                _hours.AddRange(gotHours.Where(p => _hours.Count == 0 || p.T > _hours[^1].T));
                _hours.RemoveAll(p => p.T < at - HourKeep);
                _hoursLoaded = true;
                _hoursAt = now;
            }
        }
    }

    /// <summary>
    /// sysstat: список файлов дней, что покрывают выбранный период и такой же до него (по времени изменения файла,
    /// а не по номеру дня в имени — sa01 после sa31 не путается); дни, которых нет в памяти или что изменились
    /// (сегодняшний), читаются по нескольку за заход — прочитанное сразу на экране. Время точек — из самих строк sadf.
    /// </summary>
    private async Task SysstatHistoryAsync(MonitorCollector collector, CancellationToken stop)
    {
        long window;

        lock (_gate)
        {
            window = _viewers.Values.Where(r => r != MonitorRange.Live).Select(MonitorRanges.Window).DefaultIfEmpty(0).Max();
        }

        var now = DateTime.UtcNow;

        if (_saLoaded && window <= _saWindow && now - _saAt < TimeSpan.FromSeconds(55))
        {
            return;
        }

        var days = (int)Math.Min(400, 2 * window / 86400 + 2);
        var (list, _) = await ExecAsync(MonitorHistory.ListCommand(collector.SaDir, days), stop);
        var (files, tz) = MonitorHistory.ParseList(list);

        MonitorHistory.SaContext context;

        lock (_gate)
        {
            context = new MonitorHistory.SaContext(Now?.MemTotal ?? 0, Info?.Iface ?? string.Empty, _blocks, _devNames, tz);

            // дни, что ушли из списка (старше периода или удалены), из памяти тоже уходят
            foreach (var gone in _saDays.Keys.Where(k => files.All(f => f.Path != k)).ToList())
            {
                _saDays.Remove(gone);
            }
        }

        // свежие первыми: сегодняшний день нужен всегда, старые дочитываются следом
        var todo = files
            .Where(f => !_saDays.TryGetValue(f.Path, out var known) || known.Sig != f.Sig || f.Sig == "0 0")
            .OrderByDescending(f => f.Mtime)
            .ToList();

        for (var i = 0; i < todo.Count && !stop.IsCancellationRequested; i += MonitorHistory.FilesPerRequest)
        {
            var batch = todo.Skip(i).Take(MonitorHistory.FilesPerRequest).ToList();
            var (output, _) = await ExecAsync(MonitorHistory.ReadCommand(batch.Select(f => f.Path)), stop);
            var read = MonitorHistory.Parse(output, context);

            lock (_gate)
            {
                foreach (var file in batch)
                {
                    _saDays[file.Path] = (file.Sig, read.TryGetValue(file.Path, out var day) ? day : new MonitorHistory.SaDay { Invalid = true });
                }

                RebuildSysstat();
                _saLoaded = true;
                _cacheDirty = true;
            }

            Notify();
        }

        lock (_gate)
        {
            RebuildSysstat();
            _saLoaded = true;
            _saAt = now;
            _saWindow = window;
        }
    }

    /// <summary>Все прочитанные дни — одним рядом точек по времени; что в истории вообще было — флажками.</summary>
    private void RebuildSysstat()
    {
        var valid = _saDays.Values.Select(v => v.Day).Where(d => !d.Invalid).ToList();

        _sa = valid.SelectMany(d => d.Points).GroupBy(p => p.T).Select(g => g.First()).OrderBy(p => p.T).ToList();
        _saInterval = valid.Select(d => d.Interval).DefaultIfEmpty(0).Max();
        _saCpu = valid.Any(d => d.Cpu);
        _saMem = valid.Any(d => d.Mem);
        _saNet = valid.Any(d => d.Net);
        _saDisk = valid.Any(d => d.Disk);
    }

    /// <summary>Время сервера сейчас: последнее известное плюс прошедшее с тех пор.</summary>
    private long ServerNow() =>
        _serverNow > 0
            ? _serverNow + (long)Stopwatch.GetElapsedTime(_serverNowStamp).TotalSeconds
            : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>История периода уже прочитана — или знаем, что читать нечего.</summary>
    public bool HistoryRead(MonitorRange range)
    {
        lock (_gate)
        {
            return range == MonitorRange.Live
                || (_loadedSource == HistorySource.Sysstat ? _saLoaded
                    : MonitorRanges.Hourly(range) ? _hoursLoaded && _minutesLoaded : _minutesLoaded);
        }
    }

    /// <summary>
    /// Отчёт для карточек — Report из rudog. У недели и дальше незакрытый час собирается из минутных точек,
    /// у часа и суток — незакрытая минута из файла сборщика: график доходит до «сейчас».
    /// </summary>
    public MonitorReport Report(MonitorRange range)
    {
        lock (_gate)
        {
            if (range == MonitorRange.Live)
            {
                return MonitorReport.Build(range, _live.ToList(), _live.Count > 0 ? _live[^1].T : ServerNow());
            }

            var now = ServerNow();
            var win = MonitorRanges.Window(range);
            var points = new List<MonitorPoint>();

            // sysstat: свои точки раз в несколько минут — шаг графика не мельче их, чего в истории нет — не показываем
            if (_loadedSource == HistorySource.Sysstat)
            {
                var missing = new HashSet<string>(StringComparer.Ordinal);

                if (!_saCpu) { missing.Add("cpu"); }
                if (!_saMem) { missing.Add("mem"); }
                if (!_saNet) { missing.Add("net"); }
                if (!_saDisk) { missing.Add("disk"); }

                // место на диске sysstat не пишет — изменения за период нет
                missing.Add("space");

                return MonitorReport.Build(range, _sa, now, HistorySource.Sysstat, _saInterval, _sa.Count > 0 ? missing : new HashSet<string>(StringComparer.Ordinal) { "space" });
            }

            if (MonitorRanges.Hourly(range))
            {
                var keep = (int)(2 * win / 3600 + 2);
                points.AddRange(_hours.Skip(Math.Max(0, _hours.Count - keep)));

                var hour = now / 3600 * 3600;
                var tail = _minutes.Where(p => p.T >= hour).ToList();

                if (_currentMinute is { } cur && cur.T >= hour && (tail.Count == 0 || cur.T > tail[^1].T))
                {
                    tail.Add(cur);
                }

                if (tail.Count > 0 && (points.Count == 0 || points[^1].T < hour))
                {
                    var folded = MonitorPoint.Fold(tail);
                    folded.T = hour;
                    points.Add(folded);
                }
            }
            else
            {
                var keep = (int)(2 * win / 60 + 2);
                points.AddRange(_minutes.Skip(Math.Max(0, _minutes.Count - keep)));

                if (_currentMinute is { } cur && (points.Count == 0 || cur.T > points[^1].T))
                {
                    points.Add(cur);
                }
            }

            return MonitorReport.Build(range, points, now, _loadedSource);
        }
    }

    private void Notify() => Dispatch(() => Changed?.Invoke());

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(action);
    }
}
