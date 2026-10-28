using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using MultiServerSync.Core;
using MultiServerSync.Models;

namespace MultiServerSync.Services;

public delegate Task ServerWork(ServerEntry server, OperationResult result, CancellationToken ct);

/// <summary>
/// Один прогон операции по всем отмеченным серверам: очередь, параллельность,
/// прогресс, итоговая сводка «сколько приняли успешно» и повтор по упавшим.
/// </summary>
public sealed class OperationBatch : ObservableObject
{
    private CancellationTokenSource? _cts;
    private ServerWork? _lastWork;
    private Action<IReadOnlyList<ServerEntry>>? _beforeRetry;
    private Action? _afterRetry;
    private string _operationName = string.Empty;

    /// <summary>Прогоны других вкладок, если этот прогон — сводный.</summary>
    private IReadOnlyList<OperationBatch> _sources = [];

    /// <summary>Сводка «Выполнить везде»: строки разных вкладок в одной таблице.</summary>
    public bool IsSummary => _sources.Count > 0;

    private DateTime? _finishedAt;

    private bool _isRunning;
    private int _total;
    private int _done;
    private int _success;
    private int _failed;

    public OperationBatch()
    {
        Results.CollectionChanged += (_, _) =>
        {
            Raise(nameof(HasResults));
            TerminalStore.MarkDirty();
        };
    }

    // ── терминал переживает перезапуск ────────────────────────────────────────

    /// <summary>Строки и срок жизни вывода — для UserData\terminal.json. Сводка не пишется: она из чужих строк.</summary>
    internal TerminalBatchState? ToState() => IsSummary || Results.Count == 0
        ? null
        : new TerminalBatchState
        {
            Rows = Results.Select(r => r.ToState()).ToList(),
            LifeFrom = _lifeFrom,
            FinishedAt = _finishedAt,
            StartedAt = StartedAt,
            Total = Total,
            Done = Done,
            Success = SuccessCount,
            Failed = FailedCount,
        };

    /// <summary>
    /// Строки из файла после перезапуска. Срок «Хранить лог» идёт дальше от того же момента: вышел —
    /// следующий запуск начнёт терминал заново, нет — допишет ниже. Строки удалённых серверов не встают.
    /// </summary>
    internal void Restore(TerminalBatchState state, Func<string, ServerEntry?> server, DateTime savedAt)
    {
        foreach (var rowState in state.Rows)
        {
            if (server(rowState.ServerId) is { } entry)
            {
                var row = OperationResult.FromState(rowState, entry);
                row.Batch = this;
                Results.Add(row);
            }
        }

        if (Results.Count == 0)
        {
            return;
        }

        // программа закрылась посреди работы — срок считается от закрытия
        _lifeFrom = state.LifeFrom ?? savedAt;
        _finishedAt = state.FinishedAt;
        StartedAt = state.StartedAt;
        Total = state.Total;
        Done = state.Done;
        SuccessCount = state.Success;
        FailedCount = state.Failed;
        RaiseMany(nameof(LifeText), nameof(LifeExpired), nameof(SummaryText));
    }

    public ObservableCollection<OperationResult> Results { get; } = new();

    /// <summary>Вкладка, чей это прогон: у неё своя галочка «Сохранять лог».</summary>
    public Workspace? Owner { get; set; }

    /// <summary>
    /// «Сохранять лог» вкладки: отмечено — вкладка хранит весь вывод, пока галочку не снимут,
    /// новый прогон дописывается к строкам тех же серверов через отбивку. Снято — прогоны
    /// копятся строками по времени запуска, пока между ними не больше срока жизни вывода.
    /// Сводный прогон — галочка всех вкладок, из которых он собран.
    /// </summary>
    public bool KeepLog
    {
        get => _sources.Count > 0 ? _sources.All(s => s.KeepLog) : Owner?.KeepOutputLog == true;
        set
        {
            if (_sources.Count > 0)
            {
                foreach (var source in _sources)
                {
                    source.KeepLog = value;
                }
            }
            else if (Owner is { } owner)
            {
                owner.KeepOutputLog = value;
            }

            Raise();
        }
    }

    internal void RaiseKeepLog()
    {
        // «Сохранять лог» сняли — отсчёт срока с начала, а не стирать накопленное в ту же секунду
        if (!KeepLog && !IsRunning && _lifeFrom is not null)
        {
            _lifeFrom = DateTime.Now;
            TerminalStore.MarkDirty();
        }

        RaiseMany(nameof(KeepLog), nameof(LifeText), nameof(LifeExpired));
    }

    /// <summary>
    /// Срок поменяли кнопками — отсчёт с начала нового срока, если в терминале уже есть вывод,
    /// даже когда он дошёл до нуля: следующий запуск снова допишется ниже.
    /// </summary>
    public void RestartLife()
    {
        foreach (var source in _sources)
        {
            source.RestartLife();
        }

        if (!IsRunning && Results.Count > 0 && _lifeFrom is not null)
        {
            _lifeFrom = DateTime.Now;
            TerminalStore.MarkDirty();
        }

        RaiseMany(nameof(LifeText), nameof(LifeExpired));
    }

    /// <summary>С какого момента идёт срок жизни вывода: окончание последнего запуска. Null — отсчёта нет.</summary>
    private DateTime? _lifeFrom;

    /// <summary>Сколько осталось, пока новый запуск ещё дописывается ниже; null — отсчёта нет (идёт работа, пусто или «Сохранять лог»).</summary>
    private TimeSpan? LifeLeft =>
        _sources.Count == 0 && !KeepLog && !IsRunning && Results.Count > 0 && _lifeFrom is { } from
            ? AppState.Current.OutputLife - (DateTime.Now - from)
            : null;

    /// <summary>Окошко срока: «04:37» — сколько осталось, без отсчёта — сам срок.</summary>
    public string LifeText
    {
        get
        {
            var shown = LifeLeft ?? AppState.Current.OutputLife;
            var seconds = Math.Max(0, (int)Math.Ceiling(shown.TotalSeconds));
            return $"{seconds / 60:00}:{seconds % 60:00}";
        }
    }

    /// <summary>Отсчёт дошёл до нуля: следующий запуск начнёт терминал заново — окошко оранжевое.</summary>
    public bool LifeExpired =>
        _sources.Count > 0
            ? _sources.Any(s => s.LifeExpired)
            : LifeLeft is { } left && left <= TimeSpan.Zero;

    /// <summary>
    /// Раз в секунду — отсчёт в окошке. На нуле ничего не стирается: срок лишь решает, что сделает
    /// следующий запуск — до нуля допишет свои строки ниже, после нуля начнёт терминал заново.
    /// </summary>
    internal void TickLife() => RaiseMany(nameof(LifeText), nameof(LifeExpired));

    /// <summary>Строки последнего прогона: повтор упавших — только среди них, у прошлых прогонов была своя работа.</summary>
    private IReadOnlyList<OperationResult> _lastRun = [];

    /// <summary>Строки последнего прогона — к ним терминал прокручивается при запуске.</summary>
    public IReadOnlyList<OperationResult> LastRun => _lastRun;

    /// <summary>
    /// Счёт последнего запуска — для итога на ярлыке и решений после него: таблица могла накопить
    /// и прошлые запуски. У сводного прогона — его общий счёт.
    /// </summary>
    public int RunTotal => _sources.Count > 0 ? Total : _lastRun.Count;

    public int RunSuccess => _sources.Count > 0 ? SuccessCount : _lastRun.Count(r => r.Status == OperationStatus.Success);

    public int RunFailed => _sources.Count > 0 ? FailedCount : _lastRun.Count(r => r.Status is OperationStatus.Failed or OperationStatus.Cancelled);

    /// <summary>Название вкладки, из которой запущена операция — попадает в строки результатов.</summary>
    public string? Scope { get; set; }

    /// <summary>Раздел вкладки — им помечаются записи журнала об этой операции.</summary>
    public AppSection? Section { get; set; }

    /// <summary>
    /// Команда шла через sudo: её процессы принадлежат root, и kill к ним тоже нужен через sudo.
    /// </summary>
    public bool UsedSudo { get; set; }

    public bool HasResults => Results.Count > 0;

    /// <summary>Когда начался последний прогон: команды после копирования показываются, только если они новее его.</summary>
    public DateTime? StartedAt { get; private set; }

    /// <summary>
    /// Сервер, выбранный в таблице терминала, пока он показывал этот прогон. По нему автокопия
    /// «Текущий лог» знает, чей вывод брать, даже когда терминал уже показывает другую вкладку.
    /// </summary>
    public OperationResult? Picked { get; set; }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (Set(ref _isRunning, value))
            {
                RaiseMany(nameof(IsIdle), nameof(CanRetry), nameof(SummaryText));
            }
        }
    }

    public bool IsIdle => !IsRunning;

    public int Total
    {
        get => _total;
        private set { if (Set(ref _total, value)) { RaiseMany(nameof(ProgressText), nameof(ProgressPercent), nameof(SummaryText)); } }
    }

    public int Done
    {
        get => _done;
        private set { if (Set(ref _done, value)) { RaiseMany(nameof(ProgressText), nameof(ProgressPercent), nameof(SummaryText)); } }
    }

    public int SuccessCount
    {
        get => _success;
        private set { if (Set(ref _success, value)) { Raise(nameof(SummaryText)); } }
    }

    public int FailedCount
    {
        get => _failed;
        private set { if (Set(ref _failed, value)) { RaiseMany(nameof(CanRetry), nameof(SummaryText)); } }
    }

    public bool CanRetry => !IsRunning && (_sources.Count > 0 ? FailedCount > 0 : _lastRun.Any(r => r.Status is OperationStatus.Failed or OperationStatus.Cancelled));

    public double ProgressPercent => Total == 0 ? 0 : Done * 100.0 / Total;

    public string ProgressText => Total == 0 ? string.Empty : $"{Done} из {Total}";

    /// <summary>
    /// Состояние прогона словами: пока идёт — сколько серверов прошло, после — время
    /// окончания и итог. По одному выводу не видно, закончилась команда или ещё идёт,
    /// поэтому эта строка стоит в окне вывода.
    /// </summary>
    public string SummaryText
    {
        get
        {
            if (IsRunning)
            {
                return $"выполняется: {Done} из {Total}";
            }

            if (Total == 0)
            {
                return "ещё не запускалось";
            }

            var finished = _finishedAt is { } time ? $"готово {time:HH:mm:ss} · " : string.Empty;
            var counts = $"успешно {SuccessCount} из {Total}";

            return FailedCount == 0
                ? finished + counts
                : $"{finished}{counts}, ошибок {FailedCount}";
        }
    }

    /// <summary>
    /// Весь вывод одним текстом — по одному блоку на сервер, как в терминале:
    /// «Сервер: Имя (адрес):», под ним строка команды и её ответ.
    /// </summary>
    public string BuildTranscript()
    {
        var text = new StringBuilder();

        foreach (var result in Results)
        {
            var output = result.Output.TrimEnd();

            // название вкладки — только в сводке, где рядом строки разных вкладок
            text.AppendLine(result.CopyHeader(IsSummary));

            if (output.Length > 0)
            {
                text.AppendLine(output);

                // то же время, что в таблице слева: без вывода оно уже есть в строке статуса
                if (result.TimeLine is { } time)
                {
                    text.AppendLine(time);
                }
            }
            else
            {
                text.AppendLine($"(вывода нет · {result.StatusText})");
            }

            text.AppendLine();
        }

        return text.ToString().TrimEnd() + Environment.NewLine;
    }

    /// <summary>
    /// Корзина в шапке таблицы запусков: убирает из неё законченные запуски. Идущие и ждущие очереди
    /// остаются — их работа ещё пишет в свои строки. Сводка чистит вкладки, из которых собрана.
    /// </summary>
    /// <returns>Сколько строк убрано.</returns>
    public int ClearFinished()
    {
        if (_sources.Count > 0)
        {
            return _sources.Sum(s => s.ClearFinished());
        }

        static bool Active(OperationResult row) => row.Status is OperationStatus.Running or OperationStatus.Pending;

        // у копирования под строкой могут идти его команды — строка остаётся, пока они идут
        var gone = Results.Where(r => !Active(r) && !r.Steps.Any(s => s.Status == OperationStatus.Running)).ToList();

        foreach (var row in gone)
        {
            Results.Remove(row);
        }

        _lastRun = _lastRun.Where(Results.Contains).ToList();

        if (Picked is { } picked && !Results.Contains(picked))
        {
            Picked = null;
        }

        if (Results.Count == 0)
        {
            _lifeFrom = null;
        }

        RaiseMany(nameof(CanRetry), nameof(LifeText), nameof(LifeExpired));
        return gone.Count;
    }

    private bool _stopping;

    /// <summary>
    /// «Остановить» — одна кнопка на всё, её зовут все кнопки стопа программы. Команде на сервере —
    /// сначала Ctrl+C (сигнал INT), чтобы свернулась сама; не отпустила за 3 с — KILL;
    /// и после этого ещё 2 с — закрываем канал. Копирование и скачивание процесса на сервере
    /// не держат — их обрывает сразу закрытие канала. Второе нажатие не ждёт: KILL и закрыть.
    /// </summary>
    public async Task StopAsync()
    {
        // сводный прогон ничего не выполняет сам — останавливают те, кто выполняет
        if (_sources.Count > 0)
        {
            await Task.WhenAll(_sources.Select(s => s.StopAsync()));
            return;
        }

        // «Остановить» — всё во вкладке: ждущие очереди не начнутся, иначе тут же стартовал бы следующий
        foreach (var waiting in _waiting.ToList())
        {
            CancelWaiting(waiting);
        }

        // ждущие команды цепочки не пойдут; идущие строки — «останавливается…» сразу
        foreach (var row in Results.ToList())
        {
            row.CancelStepsFrom(1);
            row.BeginStopping();
        }

        if (!IsRunning)
        {
            return;
        }

        var hasProcess = Results.Any(r => r.IsRunning && r.RemoteGroup > 0);

        if (_stopping || !hasProcess)
        {
            if (hasProcess)
            {
                await SignalAsync("KILL", "снятие (сигнал KILL)");
            }

            Cancel();
            return;
        }

        _stopping = true;

        try
        {
            await SignalAsync("INT", "Ctrl+C (сигнал INT)");

            if (await StillRunningAfter(TimeSpan.FromSeconds(3)))
            {
                await SignalAsync("KILL", "снятие (сигнал KILL)");

                if (await StillRunningAfter(TimeSpan.FromSeconds(2)))
                {
                    Cancel();
                }
            }
        }
        finally
        {
            _stopping = false;
        }
    }

    private async Task<bool> StillRunningAfter(TimeSpan wait)
    {
        var until = DateTime.UtcNow + wait;

        while (IsRunning && DateTime.UtcNow < until)
        {
            await Task.Delay(100);
        }

        return IsRunning;
    }

    /// <summary>
    /// Посылает сигнал команде на тех серверах, где она ещё идёт. Отдельным подключением:
    /// канал команды занят её выводом, а «нажать Ctrl+C» в него нельзя — стандартный ввод
    /// закрыт сразу после передачи скрипта.
    /// </summary>
    /// <param name="only">Только этим строкам — стоп в строке; без него — всем идущим.</param>
    private async Task SignalAsync(string signal, string what, IReadOnlyList<OperationResult>? only = null)
    {
        // сводный прогон ничего не выполняет сам — прерывают те, кто выполняет
        if (_sources.Count > 0)
        {
            await Task.WhenAll(_sources.Select(s => s.SignalAsync(signal, what)));
            return;
        }

        var targets = (only ?? Results).Where(r => r.IsRunning && r.RemoteGroup > 0).ToList();

        if (targets.Count == 0)
        {
            AppLog.Hint(Results.Any(r => r.IsRunning)
                ? "Сервер ещё не сообщил, каким процессом идёт команда — повторите через секунду."
                : "Прерывать нечего: команда сейчас не выполняется.",
                LogLevel.Warning);
            return;
        }

        if (Section is { } section)
        {
            AppLog.Section = section;
        }

        AppLog.Info($"{what} — на {targets.Count} сервере(ах).");

        var settings = AppState.Current.Settings;

        await Task.WhenAll(targets.Select(async result =>
        {
            result.Interrupted = true;
            result.Append($"— послан {what} —");

            try
            {
                var sent = await SshService.SignalJobAsync(
                    result.Server, settings, result.RemotePid, result.RemoteGroup, signal, UsedSudo);

                if (sent)
                {
                    AppLog.Success($"{what} доставлен", result.Server.Display);
                }
                else
                {
                    AppLog.Warn($"{what} не доставлен: процесса уже нет или не хватило прав",
                        result.Server.Display);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error($"{what} не отправлен: " + SshService.Describe(ex, result.Server),
                    result.Server.Display);
            }
        }));
    }

    private void Cancel()
    {
        if (_sources.Count > 0)
        {
            foreach (var source in _sources)
            {
                source.Cancel();
            }

            return;
        }

        if (_cts is { IsCancellationRequested: false })
        {
            AppLog.Warn("Отмена операции по запросу пользователя.");
            _cts.Cancel();
        }
    }

    /// <summary>
    /// Сводный прогон: показывает результаты сразу нескольких вкладок одной таблицей.
    /// Строки — те же объекты, что и во вкладках, поэтому статусы обновляются сами.
    /// </summary>
    public void TrackAll(IReadOnlyList<OperationBatch> sources, string operationName)
    {
        foreach (var source in _sources)
        {
            source.PropertyChanged -= OnSourceChanged;
            source.Results.CollectionChanged -= OnSourceResultsChanged;
        }

        _sources = sources;
        _operationName = operationName;

        foreach (var source in _sources)
        {
            source.PropertyChanged += OnSourceChanged;
            source.Results.CollectionChanged += OnSourceResultsChanged;
        }

        RebuildFromSources();
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e) => Recount();

    private void OnSourceResultsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildFromSources();

    private void RebuildFromSources()
    {
        Results.Clear();

        foreach (var source in _sources)
        {
            foreach (var result in source.Results)
            {
                Results.Add(result);
            }
        }

        Recount();
    }

    private void Recount()
    {
        if (_sources.Count == 0)
        {
            return;
        }

        Total = _sources.Sum(s => s.Total);
        Done = _sources.Sum(s => s.Done);
        SuccessCount = _sources.Sum(s => s.SuccessCount);
        FailedCount = _sources.Sum(s => s.FailedCount);

        var running = _sources.Any(s => s.IsRunning);

        // сводный прогон ничего не выполняет сам: он «закончился», когда затихла последняя вкладка
        if (IsRunning && !running)
        {
            _finishedAt = DateTime.Now;
        }

        IsRunning = running;
    }

    /// <param name="concurrent">
    /// Можно стартовать, пока идёт прошлый запуск: оба идут рядом в одной таблице («Проводник»).
    /// Без него идущий прогон второй запуск не принимает.
    /// </param>
    /// <param name="prepare">Каждой строке до старта: так встают строки команд после копирования.</param>
    public async Task RunAsync(
        string operationName,
        IReadOnlyList<ServerEntry> servers,
        ServerWork work,
        int maxParallel,
        Action<IReadOnlyList<ServerEntry>>? beforeRetry = null,
        Action? afterRetry = null,
        bool concurrent = false,
        Action<OperationResult>? prepare = null)
    {
        if (IsRunning && !concurrent || servers.Count == 0)
        {
            return;
        }

        _operationName = operationName;
        _lastWork = work;
        _beforeRetry = beforeRetry;
        _afterRetry = afterRetry;

        // Прогоны копятся строками в порядке запуска, сверху вниз, — хоть сто подряд: видно всё,
        // что запущено, и чем кончилось. «Сохранять лог» у вкладки — не стирается ничего, пока
        // галочку не снимут. Без неё прошлые строки уходят, только если новый прогон начат позже
        // срока «Хранить лог» после окончания прошлого (пока что-то идёт, сюда не попасть).
        var life = AppState.Current.OutputLife;

        if (!IsRunning && !KeepLog && (_lifeFrom is not { } last || DateTime.Now - last > life))
        {
            Results.Clear();

            // ждущие своей очереди — не прошлое, их строки остаются
            foreach (var waiting in _waiting)
            {
                Results.Add(waiting);
            }
        }

        // вставший в очередь запуск уже виден строкой «в очереди» — он занимает её, а не добавляет новую.
        // Только свою: копирование, стартовавшее рядом на тот же сервер, чужую ждущую строку не берёт
        var run = new List<OperationResult>();
        var claimed = ClaimedRow.Value;

        foreach (var server in servers)
        {
            var row = claimed is not null && _waiting.Contains(claimed) && ReferenceEquals(claimed.Server, server)
                ? claimed
                : null;

            if (row is not null)
            {
                _waiting.Remove(row);
                row.Message = string.Empty;
            }
            else
            {
                row = new OperationResult(server, Scope);
                Results.Add(row);
            }

            row.Batch = this;
            row.Work = work;
            prepare?.Invoke(row);
            run.Add(row);
        }

        await ExecuteAsync(run, work, maxParallel);
    }

    /// <summary>Строки запусков, вставших в очередь за идущим: видны в терминале сразу, а не со стартом.</summary>
    private readonly List<OperationResult> _waiting = [];

    /// <summary>
    /// Строка «в очереди», которую займёт запуск из этого хода работы. AsyncLocal: значение видно
    /// только вызовам, ушедшим из того места, где его поставили, — соседний запуск его не увидит.
    /// </summary>
    public static readonly AsyncLocal<OperationResult?> ClaimedRow = new();

    /// <summary>Запуск встал в очередь — его строка «в очереди · что и куда» появляется сразу.</summary>
    public OperationResult AddWaiting(ServerEntry server, string what)
    {
        var row = new OperationResult(server, Scope) { Message = "в очереди · " + what, Batch = this };
        _waiting.Add(row);
        Results.Add(row);
        return row;
    }

    /// <summary>Очередь дошла, а запуск не состоялся (нечего копировать, отказ) — строка уходит.</summary>
    public void DropWaiting(OperationResult row)
    {
        if (_waiting.Remove(row))
        {
            Results.Remove(row);
        }
    }

    /// <summary>Ждущий в очереди запуск остановили: он не начнётся, строка остаётся «не запускалось».</summary>
    private void CancelWaiting(OperationResult row)
    {
        if (!_waiting.Remove(row))
        {
            return;
        }

        row.Skipped = true;
        row.Message = "не запускалось";
        row.Status = OperationStatus.Cancelled;
    }

    /// <summary>
    /// Стоп в строке — только она, остальные идут дальше. Ждёт очереди — не начнётся. Идёт команда —
    /// так же, как общий «Остановить»: Ctrl+C, через 3 с KILL, ещё через 2 с — закрыть канал.
    /// Копирование и скачивание обрываются сразу.
    /// </summary>
    public async Task StopRowAsync(OperationResult row)
    {
        if (_waiting.Contains(row))
        {
            CancelWaiting(row);
            return;
        }

        // встал сервер — его команды цепочки, что ещё ждут, не пойдут
        row.CancelStepsFrom(1);

        if (row.Status == OperationStatus.Pending)
        {
            CancelRow(row);
            return;
        }

        if (row.Status != OperationStatus.Running || row.Stopping)
        {
            return;
        }

        // Ctrl+C идёт отдельным подключением, а команда сворачивается не мгновенно — видно сразу
        row.BeginStopping();

        if (row.RemoteGroup <= 0)
        {
            CancelRow(row);
            return;
        }

        await SignalAsync("INT", "Ctrl+C (сигнал INT)", [row]);

        if (await RowRunningAfter(row, TimeSpan.FromSeconds(3)))
        {
            await SignalAsync("KILL", "снятие (сигнал KILL)", [row]);

            if (await RowRunningAfter(row, TimeSpan.FromSeconds(2)))
            {
                CancelRow(row);
            }
        }
    }

    private static void CancelRow(OperationResult row)
    {
        try
        {
            row.Stopper?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // строка как раз закончилась сама
        }
    }

    private static async Task<bool> RowRunningAfter(OperationResult row, TimeSpan wait)
    {
        var until = DateTime.UtcNow + wait;

        while (row.IsRunning && DateTime.UtcNow < until)
        {
            await Task.Delay(100);
        }

        return row.IsRunning;
    }

    public async Task RetryFailedAsync(int maxParallel)
    {
        // сводный прогон сам ничего не выполняет — повторяем у тех, кто выполнял
        if (_sources.Count > 0)
        {
            await Task.WhenAll(_sources.Select(s => s.RetryFailedAsync(maxParallel)));
            return;
        }

        if (IsRunning || _lastWork is null)
        {
            return;
        }

        var failed = _lastRun.Where(r => r.Status is OperationStatus.Failed or OperationStatus.Cancelled).ToList();
        if (failed.Count == 0)
        {
            return;
        }

        AppLog.Info($"Повтор операции «{_operationName}» на {failed.Count} сервере(ах).");

        var header = RunHeader("повтор: " + _operationName);

        foreach (var r in failed)
        {
            r.ResetJob();

            if (KeepLog)
            {
                r.StartNextRun(header);
            }
            else
            {
                r.Reset();
            }
        }

        // цепочке команд надо знать, кто повторяет: счётчики окон и итог на ярлыке
        _beforeRetry?.Invoke(failed.Select(r => r.Server).ToList());

        try
        {
            // у запусков, шедших рядом («Проводник»), работа своя — каждая строка повторяет свою
            var lastWork = _lastWork;
            await Task.WhenAll(failed
                .GroupBy(r => r.Work ?? lastWork)
                .Select(g => ExecuteAsync(g.ToList(), g.Key, maxParallel)));
        }
        finally
        {
            _afterRetry?.Invoke();
        }
    }

    /// <summary>Отбивка между прогонами в выводе, когда лог сохраняется.</summary>
    private static string RunHeader(string operationName) =>
        $"───── {DateTime.Now:HH:mm:ss} · {operationName} ─────";

    /// <summary>Достаёт настоящую причину из обёрток вроде AggregateException.</summary>
    private static Exception Unwrap(Exception ex)
    {
        if (ex is AggregateException aggregate)
        {
            return Unwrap(aggregate.GetBaseException());
        }

        // сетевые сбои SSH.NET часто приходят завёрнутыми
        if (ex.InnerException is System.Net.Sockets.SocketException socket)
        {
            return socket;
        }

        return ex;
    }

    /// <summary>Запусков, идущих сейчас: «Проводник» пускает несколько рядом.</summary>
    private int _activeRuns;

    private async Task ExecuteAsync(IReadOnlyList<OperationResult> targets, ServerWork work, int maxParallel)
    {
        // первый запуск — новая общая отмена и новый счёт; следующий рядом — к идущим
        var first = _activeRuns == 0;

        if (first)
        {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _finishedAt = null;
        }

        var ct = _cts!.Token;
        _activeRuns++;

        StartedAt = DateTime.Now;
        Raise(nameof(StartedAt));

        // итоги серверов придут в журнал уже после того, как человек мог уйти в другой раздел
        if (Section is { } section)
        {
            AppLog.Section = section;
        }

        if (first)
        {
            // счёт — по всей таблице: прошлые прогоны в ней уже закончены, «готово 3 из 4» видно сразу;
            // ждущие очереди — ещё не начаты, их нет в счёте
            var earlier = Results.Where(r => !targets.Contains(r) && !_waiting.Contains(r)).ToList();

            if (!ReferenceEquals(targets, _lastRun) && !targets.All(_lastRun.Contains))
            {
                _lastRun = targets.ToList();
            }

            IsRunning = true;
            Total = earlier.Count + targets.Count;
            Done = earlier.Count;
            SuccessCount = earlier.Count(r => r.Status == OperationStatus.Success);
            FailedCount = Done - SuccessCount;
        }
        else
        {
            _lastRun = _lastRun.Concat(targets.Where(r => !_lastRun.Contains(r))).ToList();
            Total += targets.Count;
        }

        // серверов этого запуска, а не строк в таблице: прошлые запуски в ней тоже лежат
        AppLog.Debug($"«{_operationName}» — старт на {targets.Count} сервере(ах), одновременно до {maxParallel}.");

        using var gate = new SemaphoreSlim(Math.Max(1, maxParallel));
        var doneLock = new Lock();

        var tasks = targets.Select(async result =>
        {
            // своя отмена у строки — стоп в строке; общая «Остановить» гасит и её
            using var rowCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            result.Stopper = rowCts;
            var rowCt = rowCts.Token;
            var entered = false;
            var sw = new Stopwatch();

            try
            {
                try
                {
                    await gate.WaitAsync(rowCt);
                    entered = true;
                }
                catch (OperationCanceledException)
                {
                    // остановили, пока ждала своей очереди среди серверов
                }

                if (!entered || rowCt.IsCancellationRequested)
                {
                    result.Skipped = true;
                    result.Message = "не запускалось";
                    result.Status = OperationStatus.Cancelled;
                    return;
                }

                sw.Start();
                result.Status = OperationStatus.Running;
                result.Message = "подключение…";
                AppLog.Debug($"старт: {_operationName}", result.Server.Display);

                await work(result.Server, result, rowCt);

                if (result.Status == OperationStatus.Running)
                {
                    result.Status = OperationStatus.Success;
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException || rowCt.IsCancellationRequested)
            {
                // после «Остановить» всё, что оборвалось, — выполненная просьба, а не сбой:
                // закрытый канал SSH.NET сообщает своей ошибкой, а не отменой. Что успело уйти — на месте
                result.Message = result.StoppedText;
                result.Status = OperationStatus.Cancelled;
                result.Append("— остановлено —");
            }
            catch (Exception ex)
            {
                var real = Unwrap(ex);
                var explained = SshService.Describe(real, result.Server);

                result.Status = OperationStatus.Failed;
                result.Message = explained;
                result.Append("ОШИБКА: " + explained);

                if (!string.Equals(explained, real.Message, StringComparison.Ordinal))
                {
                    result.Append($"Исходная ошибка ({real.GetType().Name}): {real.Message}");
                }

                if (real is Renci.SshNet.Common.SshAuthenticationException)
                {
                    var methods = await SshService.TryProbeAuthMethodsAsync(result.Server, AppState.Current.Settings);

                    if (methods.Count > 0)
                    {
                        result.Append("Сервер разрешает способы входа: " + string.Join(", ", methods));

                        if (!result.Server.HasKey && !methods.Contains("password") && !methods.Contains("keyboard-interactive"))
                        {
                            result.Append("Вход по паролю на этом сервере отключён — укажите ключ SSH в настройках сервера.");
                        }
                        else if (result.Server.HasKey && !methods.Contains("publickey"))
                        {
                            result.Append("Вход по ключу на этом сервере отключён.");
                        }
                    }
                }
            }
            finally
            {
                sw.Stop();
                result.Duration = sw.Elapsed;
                result.Stopper = null;
                result.Server.LastStatus = result.StatusText;

                lock (doneLock)
                {
                    Done++;

                    if (result.Status == OperationStatus.Success)
                    {
                        SuccessCount++;
                    }
                    else
                    {
                        FailedCount++;
                    }
                }

                // с одним сервером его итог и есть итог операции — он придёт ниже, один раз
                if (targets.Count > 1)
                {
                    AppLog.ServerLine(LevelOf(result), Outcome(result), result.Server.Display);
                }

                if (entered)
                {
                    gate.Release();
                }
            }
        });

        await Task.WhenAll(tasks);

        // прогон закончился, когда закончился последний из шедших рядом
        if (--_activeRuns == 0)
        {
            _finishedAt = DateTime.Now;
            _lifeFrom = _finishedAt;
            TerminalStore.MarkDirty();
            IsRunning = false;
        }

        Raise(nameof(CanRetry));

        if (targets.Count == 1)
        {
            var only = targets[0];
            AppLog.Write(LevelOf(only), $"{_operationName}: {Outcome(only)}", only.Server.Display);
        }
        else
        {
            // итог — про этот прогон, а не про всю накопленную таблицу
            var bad = targets
                .Where(r => r.Status is OperationStatus.Failed or OperationStatus.Cancelled)
                .Select(r => r.Server.PaneLabel)
                .ToList();

            if (bad.Count == 0)
            {
                AppLog.Success($"{_operationName}: все {targets.Count} сервер(ов) — успешно.");
            }
            else
            {
                AppLog.Error($"{_operationName}: успешно {targets.Count - bad.Count} из {targets.Count}. Не прошли: {string.Join(", ", bad)}.");
            }
        }
    }

    /// <summary>
    /// Итог сервера словами — то же, что в его строке терминала: «получено 0 · такие же 3»,
    /// а не «готово». По нему видно, что именно сделано, без похода в терминал.
    /// </summary>
    private static string Outcome(OperationResult result)
    {
        var text = result.Message.Trim().TrimEnd('…').Trim();

        if (text.Length == 0)
        {
            text = result.Status switch
            {
                OperationStatus.Success => "выполнено",
                OperationStatus.Cancelled => "отменено",
                _ => "не выполнено",
            };
        }

        return $"{text} ({result.Duration.TotalSeconds:0.0} с)";
    }

    private static LogLevel LevelOf(OperationResult result) => result.Status switch
    {
        OperationStatus.Success => LogLevel.Success,
        OperationStatus.Cancelled => LogLevel.Warning,
        _ => LogLevel.Error,
    };
}
