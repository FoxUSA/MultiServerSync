using System.Text;
using System.Windows.Media;
using MultiServerSync.Core;
using MultiServerSync.Services;
using Wpf.Ui.Controls;

namespace MultiServerSync.Models;

public enum OperationStatus
{
    Pending,
    Running,
    Success,
    Failed,
    Cancelled,
}

/// <summary>Строка результата по одному серверу.</summary>
public sealed class OperationResult : ObservableObject
{
    private static readonly SolidColorBrush Grey = Freeze(Color.FromRgb(0x8A, 0x8A, 0x8A));
    private static readonly SolidColorBrush Blue = Freeze(Color.FromRgb(0x4C, 0xA6, 0xFF));
    private static readonly SolidColorBrush Green = Freeze(Color.FromRgb(0x4C, 0xC2, 0x6A));
    private static readonly SolidColorBrush Red = Freeze(Color.FromRgb(0xE8, 0x5C, 0x5C));
    private static readonly SolidColorBrush Orange = Freeze(Color.FromRgb(0xE8, 0xA5, 0x3C));

    private readonly StringBuilder _output = new();
    private readonly Lock _sync = new();

    private string _scope = string.Empty;
    private OperationStatus _status = OperationStatus.Pending;
    private string _message = "в очереди";
    private TimeSpan _duration;

    /// <summary>Незаконченная строка вывода — обычно вопрос, ждущий ответа.</summary>
    private string _tail = string.Empty;

    /// <summary>Когда команда пошла — от этого считается живое время.</summary>
    private DateTime _startedUtc;

    /// <summary>Идущие сейчас строки: раз в секунду им обновляется время на экране.</summary>
    private static readonly HashSet<OperationResult> Ticking = [];
    private static readonly Lock TickSync = new();
    private static readonly Timer Ticker = new(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    private CommandInput? _input;
    private bool _waitingInput;

    public OperationResult(ServerEntry server, string? scope = null)
    {
        Server = server;
        _scope = scope ?? string.Empty;
    }

    public ServerEntry Server { get; }

    /// <summary>
    /// Когда строка появилась. Прогоны копятся, у сервера строк может быть несколько — по этому
    /// времени строка команд после копирования находит своё копирование.
    /// </summary>
    public DateTime Created { get; } = DateTime.Now;

    /// <summary>Вкладка, из которой запущена операция. Пусто, когда вкладка одна.</summary>
    public string Scope
    {
        get => _scope;
        set
        {
            if (Set(ref _scope, value ?? string.Empty))
            {
                RaiseMany(nameof(ScopedShortTitle), nameof(ScopedFullTitle));
            }
        }
    }

    public string Title => Server.Display;

    /// <summary>Короткое имя для узкой колонки таблицы — полное показывает подсказка.</summary>
    public string ShortTitle => Server.PaneLabel;

    /// <summary>Полное имя — для заголовка вывода.</summary>
    public string FullTitle => Title;

    /// <summary>
    /// То же с названием вкладки — только для сводки «Выполнить везде», где в одной таблице
    /// строки разных вкладок. В терминале своей вкладки название лишнее: она и так видна.
    /// </summary>
    public string ScopedShortTitle => Scope.Length == 0 ? ShortTitle : $"{Scope} · {ShortTitle}";

    public string ScopedFullTitle => Scope.Length == 0 ? FullTitle : $"{Scope} · {FullTitle}";

    /// <summary>Подпись для текста в буфер обмена: имя и адрес сервера, в сводке — ещё и вкладка.</summary>
    public string CaptionFor(bool summary) =>
        summary && Scope.Length > 0 ? $"{Scope} · {Server.Caption}" : Server.Caption;

    /// <summary>Шапка вывода в буфере обмена — чтобы скопированный лог было с чем связать.</summary>
    public string CopyHeader(bool summary) => $"Сервер: {CaptionFor(summary)}:";

    public string User => Server.User;

    /// <summary>
    /// Номер процесса команды на сервере и номер его группы процессов — сервер сообщает их
    /// первой же строкой. По ним посылается Ctrl+C: в канал самой команды сигнал не передать.
    /// </summary>
    public int RemotePid { get; set; }

    public int RemoteGroup { get; set; }

    /// <summary>Команду оборвали нашей же кнопкой — это не ошибка, а просьба остановиться.</summary>
    public bool Interrupted { get; set; }

    public OperationStatus Status
    {
        get => _status;
        set
        {
            if (Set(ref _status, value))
            {
                TrackTime(value == OperationStatus.Running);

                // полоса прошлой передачи новому запуску не нужна: свою он выставит сам
                if (value == OperationStatus.Running)
                {
                    _transferTotal = 0;
                }

                // остановка закончилась — так или иначе
                if (value != OperationStatus.Running)
                {
                    _stopping = false;
                }

                Raise(nameof(HasTransfer));

                // закончил — с этого момента идёт столбец «Прошло»; снова в очереди или в работе — пусто
                _finishedUtc = value is OperationStatus.Success or OperationStatus.Failed or OperationStatus.Cancelled
                    ? DateTime.UtcNow
                    : null;
                Raise(nameof(AgoText));

                RaiseMany(
                    nameof(StatusSymbol),
                    nameof(StatusBrush),
                    nameof(StatusText),
                    nameof(ShortStatus),
                    nameof(IsRunning),
                    nameof(CanStop),
                    nameof(ElapsedText),
                    nameof(DurationText));
            }
        }
    }

    public string Message
    {
        get => _message;
        set { if (Set(ref _message, value)) { RaiseMany(nameof(StatusText), nameof(ShortStatus)); } }
    }

    public TimeSpan Duration
    {
        get => _duration;
        set { if (Set(ref _duration, value)) { RaiseMany(nameof(StatusText), nameof(DurationText), nameof(ElapsedText)); } }
    }

    /// <summary>Короткий статус для компактной таблицы.</summary>
    public string ShortStatus
    {
        get
        {
            if (StepNumber > 0)
            {
                return StepStatus;
            }

            var text = Status switch
            {
                OperationStatus.Success => string.IsNullOrWhiteSpace(Message) ? "успешно" : Message,
                OperationStatus.Failed => "ошибка",
                OperationStatus.Running when Stopping => "останавливается…",
                OperationStatus.Running => string.IsNullOrWhiteSpace(Message) ? "выполняется" : Message,

                // «остановлено · передано 2 МБ из 20 МБ», «не запускалось»
                OperationStatus.Cancelled => string.IsNullOrWhiteSpace(Message) ? "отменено" : Message,

                // ждущий своей очереди запуск пишет, что и куда пойдёт
                _ => string.IsNullOrWhiteSpace(Message) ? "в очереди" : Message,
            };

            return text.Length > 60 ? text[..57] + "…" : text;
        }
    }

    // ── остановка одной строки ────────────────────────────────────────────────

    /// <summary>Прогон, который выполняет эту строку: стоп в строке останавливает её через него.</summary>
    internal OperationBatch? Batch { get; set; }

    /// <summary>Работа этой строки — повтор упавших повторяет у каждой её собственную.</summary>
    internal ServerWork? Work { get; set; }

    /// <summary>Отмена только этой строки; связана с общей отменой прогона.</summary>
    internal CancellationTokenSource? Stopper { get; set; }

    private bool _skipped;

    /// <summary>Остановили раньше, чем строка началась: «не запускалось», серым.</summary>
    public bool Skipped
    {
        get => _skipped;
        set { if (Set(ref _skipped, value)) { RaiseMany(nameof(StatusBrush), nameof(StatusSymbol), nameof(ShortStatus), nameof(StatusText)); } }
    }

    private bool _stopping;

    /// <summary>
    /// Стоп нажат, строка ещё идёт: команде послан Ctrl+C, ждём, пока свернётся. Видно сразу —
    /// «останавливается…», а кнопка стопа уходит, чтобы не жать её снова и снова.
    /// </summary>
    public bool Stopping
    {
        get => _stopping;
        private set { if (Set(ref _stopping, value)) { RaiseMany(nameof(CanStop), nameof(ShortStatus), nameof(StatusText)); } }
    }

    /// <summary>Пошла остановка: эта строка и её идущая команда цепочки — «останавливается…».</summary>
    internal void BeginStopping()
    {
        if (!IsRunning)
        {
            return;
        }

        Stopping = true;

        foreach (var step in Steps.Where(s => s.IsRunning))
        {
            step.Stopping = true;
        }
    }

    /// <summary>Строку ещё можно остановить: ждёт своей очереди или идёт и стоп ещё не нажат.</summary>
    public bool CanStop => !Stopping && Status is OperationStatus.Pending or OperationStatus.Running;

    /// <summary>
    /// Стоп в строке. Команда цепочки, что ещё не пошла, не пойдёт — и нижние тоже: они ждут её итога,
    /// как при ошибке. Идущая останавливает свой сервер в цепочке.
    /// </summary>
    public Task StopAsync()
    {
        if (StepNumber > 0 && Status == OperationStatus.Pending)
        {
            Parent?.CancelStepsFrom(StepNumber);
            return Task.CompletedTask;
        }

        var row = Parent ?? this;
        return row.Batch?.StopRowAsync(row) ?? Task.CompletedTask;
    }

    /// <summary>Итог оборванной строки: сколько успело уйти — то, что уже передано, лежит на месте.</summary>
    internal string StoppedText => _transferTotal > 0
        ? $"остановлено · передано {TransferItem.Format(_transferDone)} из {TransferItem.Format(_transferTotal)}"
        : "остановлено";

    // ── команда цепочки — своей строкой ───────────────────────────────────────

    private readonly List<OperationResult> _steps = [];

    /// <summary>
    /// Строка сервера в цепочке, если это строка одной её команды. Пока команды ждут копирования —
    /// строка копирования, с началом цепочки — строка цепочки.
    /// </summary>
    public OperationResult? Parent { get; private set; }

    /// <summary>Номер команды в цепочке, если это строка одной команды; 0 — обычная строка сервера.</summary>
    public int StepNumber { get; private init; }

    /// <summary>Первая строка команды — для статуса «#1 успешно · команда».</summary>
    public string StepCommand { get; private init; } = string.Empty;

    /// <summary>
    /// Команды цепочки на этом сервере, каждая своей строкой со своим выводом и статусом.
    /// Терминал копирования показывает их под строкой копирования вместо одной общей.
    /// </summary>
    public IReadOnlyList<OperationResult> Steps
    {
        get
        {
            lock (_sync)
            {
                return _steps.ToList();
            }
        }
    }

    /// <summary>Появилась строка команды или прогон начался заново.</summary>
    public event Action<OperationResult>? StepsChanged;

    /// <summary>
    /// Команда цепочки пошла: её строка. Заранее поставленная («ждёт копирования») идёт в ход,
    /// повтор той же команды заменяет прежнюю строку.
    /// </summary>
    public OperationResult BeginStep(int number, string command)
    {
        var step = StepAt(number) is { Status: OperationStatus.Pending } planned ? planned : NewStep(number, command);
        step.Message = string.Empty;
        step.Status = OperationStatus.Running;
        StepsChanged?.Invoke(this);
        return step;
    }

    /// <summary>Цепочка встала раньше этой команды: её строка «#3 не запускалась». Отменённая стопом так и остаётся.</summary>
    public void SkipStep(int number, string command)
    {
        var step = StepAt(number);

        if (step is { Status: OperationStatus.Cancelled })
        {
            return;
        }

        if (step is not { Status: OperationStatus.Pending })
        {
            step = NewStep(number, command);
        }

        step.Message = string.Empty;
        step.Skipped = true;
        step.Status = OperationStatus.Cancelled;
        StepsChanged?.Invoke(this);
    }

    /// <summary>
    /// Команды после копирования встают строками сразу, со стартом копирования, — «ждёт копирования».
    /// Их видно заранее и любую можно остановить, пока она не пошла.
    /// </summary>
    public void PlanSteps(IEnumerable<(int Number, string Text)> steps)
    {
        foreach (var (number, text) in steps)
        {
            NewStep(number, text).Message = "ждёт копирования";
        }

        StepsChanged?.Invoke(this);
    }

    /// <summary>Цепочка пошла: заранее поставленные строки команд переходят к её строке — «в очереди».</summary>
    public void AdoptSteps(OperationResult planned)
    {
        lock (_sync)
        {
            foreach (var step in planned.Steps)
            {
                step.Parent = this;

                if (step.Status == OperationStatus.Pending)
                {
                    step.Message = string.Empty;
                }

                _steps.RemoveAll(s => s.StepNumber == step.StepNumber);
                _steps.Add(step);
            }
        }

        StepsChanged?.Invoke(this);
    }

    /// <summary>Стоп по ещё не начатой команде: не пойдут ни она, ни нижние — «#2 отменена».</summary>
    public void CancelStepsFrom(int number)
    {
        foreach (var step in Steps.Where(s => s.StepNumber >= number && s.Status == OperationStatus.Pending))
        {
            step.Skipped = true;
            step.Message = "отменена";
            step.Status = OperationStatus.Cancelled;
        }
    }

    /// <summary>Цепочка до этих команд не дошла (копирование не прошло): ждущие — «не запускалась».</summary>
    public void SkipPlanned()
    {
        foreach (var step in Steps.Where(s => s.Status == OperationStatus.Pending))
        {
            step.Message = string.Empty;
            step.Skipped = true;
            step.Status = OperationStatus.Cancelled;
        }
    }

    /// <summary>Эту команду цепочки остановили, пока она ждала.</summary>
    public bool StepStopped(int number) => StepAt(number) is { Status: OperationStatus.Cancelled };

    /// <summary>Остановлены все заранее поставленные команды — на сервере цепочке делать нечего.</summary>
    public bool StepsStopped
    {
        get
        {
            var steps = Steps;
            return steps.Count > 0 && steps.All(s => s.Status == OperationStatus.Cancelled);
        }
    }

    private OperationResult? StepAt(int number)
    {
        lock (_sync)
        {
            return _steps.FirstOrDefault(s => s.StepNumber == number);
        }
    }

    private OperationResult NewStep(int number, string command)
    {
        var step = new OperationResult(Server, Scope)
        {
            Parent = this,
            StepNumber = number,
            StepCommand = command.Replace("\r\n", "\n").Split('\n')[0].Trim(),
            Message = string.Empty,
        };

        lock (_sync)
        {
            _steps.RemoveAll(s => s.StepNumber == number);
            _steps.Add(step);
        }

        return step;
    }

    private void ClearSteps()
    {
        lock (_sync)
        {
            if (_steps.Count == 0)
            {
                return;
            }

            _steps.Clear();
        }

        StepsChanged?.Invoke(this);
    }

    /// <summary>«#1 выполняется…», «#1 успешно · команда», «#2 ошибка · код 1» — какая команда и чем кончилась.</summary>
    private string StepStatus
    {
        get
        {
            var label = $"#{StepNumber}";
            var text = Status switch
            {
                OperationStatus.Success => $"{label} успешно · {StepCommand}",
                OperationStatus.Failed => string.IsNullOrWhiteSpace(Message) ? $"{label} ошибка" : $"{label} ошибка · {Message}",
                OperationStatus.Running when Stopping => $"{label} останавливается…",
                OperationStatus.Running => $"{label} " + (string.IsNullOrWhiteSpace(Message) ? "выполняется…" : Message),

                // «#2 отменена» — остановили, пока ждала; «#3 не запускалась» — цепочка встала раньше
                OperationStatus.Cancelled when Skipped => $"{label} " + (string.IsNullOrWhiteSpace(Message) ? "не запускалась" : Message),
                OperationStatus.Cancelled => $"{label} прервано",

                // «#1 ждёт копирования», «#2 в очереди»
                _ => $"{label} " + (string.IsNullOrWhiteSpace(Message) ? "в очереди" : Message),
            };

            return text.Length > 90 ? text[..87] + "…" : text;
        }
    }

    /// <summary>Столбец «Время»: пока идёт — тикает целыми секундами, по окончании — итог.</summary>
    public string DurationText => IsRunning
        ? $"{(DateTime.UtcNow - _startedUtc).TotalSeconds:0} с"
        : Duration > TimeSpan.Zero ? $"{Duration.TotalSeconds:0.0} с" : string.Empty;

    /// <summary>Когда сервер закончил — от этого считается «Прошло».</summary>
    private DateTime? _finishedUtc;

    /// <summary>
    /// Столбец «Прошло»: сколько времени назад сервер закончил, чч:мм:сс. Тикает раз в секунду
    /// по таймеру таблицы (<see cref="RaiseAgo"/>), пока операция идёт — пусто.
    /// </summary>
    public string AgoText
    {
        get
        {
            if (_finishedUtc is not { } finished)
            {
                return string.Empty;
            }

            var ago = DateTime.UtcNow - finished;
            return $"{(int)ago.TotalHours:00}:{ago.Minutes:00}:{ago.Seconds:00}";
        }
    }

    public void RaiseAgo()
    {
        if (_finishedUtc is not null)
        {
            Raise(nameof(AgoText));
        }
    }

    /// <summary>
    /// Время в углу заголовка вывода: «идёт 1:23», пока команда работает, и «за 1:23»
    /// по окончании — чтобы было видно, сколько она уже идёт, а не только итог.
    /// </summary>
    public string ElapsedText => IsRunning
        ? "идёт " + Clock(DateTime.UtcNow - _startedUtc)
        : Duration > TimeSpan.Zero ? "за " + Clock(Duration) : string.Empty;

    private static string Clock(TimeSpan time) => time.TotalHours >= 1
        ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
        : $"{time.Minutes}:{time.Seconds:00}";

    private void TrackTime(bool running)
    {
        lock (TickSync)
        {
            if (running)
            {
                _startedUtc = DateTime.UtcNow;
                Ticking.Add(this);
            }
            else
            {
                Ticking.Remove(this);
            }

            Ticker.Change(Ticking.Count > 0 ? 1000 : Timeout.Infinite, Ticking.Count > 0 ? 1000 : Timeout.Infinite);
        }
    }

    private static void Tick()
    {
        List<OperationResult> running;

        lock (TickSync)
        {
            running = Ticking.ToList();
        }

        foreach (var result in running)
        {
            result.RaiseMany(nameof(ElapsedText), nameof(DurationText));
        }
    }

    private long _transferDone;
    private long _transferTotal;
    private DateTime _transferStartUtc;

    /// <summary>
    /// Сколько байт ушло или пришло из скольких — полоса внизу вывода. 0 из 0 — полосы нет:
    /// передавать нечего или передача ещё не началась.
    /// </summary>
    public void SetTransfer(long done, long total)
    {
        if (total > 0 && _transferTotal == 0)
        {
            _transferStartUtc = DateTime.UtcNow;
        }

        // посланное повторно (пачка не встала и ушла по одному) не должно увести полосу за край
        _transferDone = Math.Clamp(done, 0, Math.Max(total, 0));
        _transferTotal = Math.Max(total, 0);
        RaiseMany(nameof(HasTransfer), nameof(TransferPercent), nameof(TransferText));
    }

    public bool HasTransfer => IsRunning && _transferTotal > 0;

    public double TransferPercent => _transferTotal > 0 ? 100.0 * _transferDone / _transferTotal : 0;

    /// <summary>«240 МБ из 1,8 ГБ · 13% · осталось ≈ 9:40» — оценка времени, когда уже есть по чему судить.</summary>
    public string TransferText
    {
        get
        {
            if (_transferTotal <= 0)
            {
                return string.Empty;
            }

            var text = $"{TransferItem.Format(_transferDone)} из {TransferItem.Format(_transferTotal)} · {TransferPercent:0}%";
            var spent = DateTime.UtcNow - _transferStartUtc;

            if (_transferDone > 0 && _transferDone < _transferTotal && spent.TotalSeconds >= 3)
            {
                var left = TimeSpan.FromSeconds(spent.TotalSeconds * (_transferTotal - _transferDone) / _transferDone);
                text += " · осталось ≈ " + Clock(left);
            }

            return text;
        }
    }

    /// <summary>Строка под выводом при копировании в буфер: сколько шла работа на сервере.</summary>
    public string? TimeLine => Duration > TimeSpan.Zero ? $"— выполнено за {DurationText} —" : null;

    public string Output
    {
        get
        {
            lock (_sync)
            {
                return _tail.Length == 0 ? _output.ToString() : _output + _tail;
            }
        }
    }

    public bool IsRunning => Status == OperationStatus.Running;

    /// <summary>Ввод идущей команды — пока он есть, под выводом видна строка ответа.</summary>
    public CommandInput? Input
    {
        get => _input;
        set { if (Set(ref _input, value)) { Raise(nameof(CanAnswer)); } }
    }

    public bool CanAnswer => _input is not null;

    /// <summary>Команда остановилась на вопросе и ждёт ответа.</summary>
    public bool IsWaitingInput
    {
        get => _waitingInput;
        set => Set(ref _waitingInput, value);
    }

    /// <summary>Показывает незаконченную строку в конце вывода; пусто — убирает.</summary>
    public void SetTail(string tail)
    {
        lock (_sync)
        {
            _tail = tail;
        }

        Raise(nameof(Output));
    }

    public SymbolRegular StatusSymbol => Status switch
    {
        OperationStatus.Cancelled when Skipped => SymbolRegular.Circle24,
        OperationStatus.Success => SymbolRegular.CheckmarkCircle24,
        OperationStatus.Failed => SymbolRegular.ErrorCircle24,
        OperationStatus.Running => SymbolRegular.ArrowSync24,
        OperationStatus.Cancelled => SymbolRegular.Prohibited24,
        _ => SymbolRegular.Circle24,
    };

    public Brush StatusBrush => Status switch
    {
        OperationStatus.Cancelled when Skipped => Grey,
        OperationStatus.Success => Green,
        OperationStatus.Failed => Red,
        OperationStatus.Running => Blue,
        OperationStatus.Cancelled => Orange,
        _ => Grey,
    };

    public string StatusText
    {
        get
        {
            var parts = new List<string>();

            parts.Add(Status switch
            {
                OperationStatus.Success => "успешно",
                OperationStatus.Failed => "ошибка",
                OperationStatus.Running => Stopping ? "останавливается…" : "выполняется",

                // своё сообщение остановки целиком: «остановлено · передано …», «не запускалось»
                OperationStatus.Cancelled => string.IsNullOrWhiteSpace(Message) ? "отменено" : Message,
                _ => "в очереди",
            });

            // «выполняется…» в сообщении повторяет сам статус — второй раз его не пишем
            if (!string.IsNullOrWhiteSpace(Message)
                && !string.Equals(Message.TrimEnd('…', '.', ' '), parts[0], StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(Message);
            }

            if (Duration > TimeSpan.Zero)
            {
                parts.Add($"{Duration.TotalSeconds:0.0} с");
            }

            return string.Join(" · ", parts);
        }
    }

    public void Append(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        lock (_sync)
        {
            _tail = string.Empty;
            _output.AppendLine(line.TrimEnd('\r', '\n'));
        }

        Raise(nameof(Output));
    }

    /// <summary>
    /// Убирает накопленный вывод, не трогая статус строки: корзина в заголовке вывода.
    /// Нужна при сохранении лога — иначе прогоны копятся, пока не сменится набор серверов.
    /// </summary>
    /// <summary>Новый прогон: прошлые номера процессов и отметка прерывания к нему не относятся.</summary>
    public void ResetJob()
    {
        RemotePid = 0;
        RemoteGroup = 0;
        Interrupted = false;
    }

    public void ClearOutput()
    {
        lock (_sync)
        {
            _output.Clear();
            _tail = string.Empty;
        }

        Raise(nameof(Output));
    }

    /// <summary>
    /// Новый прогон в тот же вывод: прежние строки остаются, между прогонами — отбивка.
    /// Так работает галочка «Сохранять лог»; без неё строка результата создаётся заново.
    /// </summary>
    public void StartNextRun(string header)
    {
        lock (_sync)
        {
            if (_output.Length > 0)
            {
                _output.AppendLine();
                _output.AppendLine(header);
                _output.AppendLine();
            }
        }

        Duration = TimeSpan.Zero;
        Message = string.Empty;
        Skipped = false;
        Status = OperationStatus.Pending;
        ClearSteps();
        Raise(nameof(Output));
    }

    public void Reset()
    {
        lock (_sync)
        {
            _output.Clear();
            _tail = string.Empty;
        }

        Duration = TimeSpan.Zero;
        Message = string.Empty;
        Skipped = false;
        Status = OperationStatus.Pending;
        ClearSteps();
        Raise(nameof(Output));
    }

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
