using System.Diagnostics;
using MultiServerSync.Core;

namespace MultiServerSync.Models;

/// <summary>Окно команды как оно лежит в файле: текст и sudo.</summary>
public sealed class SavedCommandStep
{
    public string Text { get; set; } = string.Empty;

    public bool UseSudo { get; set; }
}

public enum StepState
{
    /// <summary>Не запускалось с открытия или не входило в последний прогон.</summary>
    Idle,

    /// <summary>В прогоне, но ни один сервер до него ещё не дошёл.</summary>
    Waiting,

    Running,

    Ok,

    /// <summary>Хоть на одном сервере не удалось или завершилось не нулём.</summary>
    Failed,

    /// <summary>Ни один сервер до него не дошёл: раньше что-то встало.</summary>
    Skipped,
}

/// <summary>
/// Одно окно многозадачности на странице «Команда». Окна идут цепочкой: следующее начинается
/// на сервере только после успешного предыдущего на этом же сервере. Номер, секундомер
/// и статус — по всем серверам прогона сразу.
/// </summary>
public sealed class CommandStep : ObservableObject
{
    private readonly Lock _gate = new();
    private readonly Stopwatch _clock = new();

    private string _text = string.Empty;
    private bool _useSudo;
    private int _number = 1;
    private int _count = 1;
    private StepState _state;
    private int _pending;
    private int _ok;
    private bool _failed;

    /// <summary>Текст или sudo поменялись — вкладке пора сохраниться.</summary>
    public event Action? Changed;

    public string Text
    {
        get => _text;
        set { if (Set(ref _text, value ?? string.Empty)) { Raise(nameof(IsEmpty)); Changed?.Invoke(); } }
    }

    public bool UseSudo
    {
        get => _useSudo;
        set { if (Set(ref _useSudo, value)) { Changed?.Invoke(); } }
    }

    // ── место в списке окон ─────────────────────────────────────────────────

    public int Number => _number;

    public bool IsFirst => _number == 1;

    /// <summary>Есть окна ниже — тогда у окна есть «выполнить это и все ниже».</summary>
    public bool HasBelow => _number < _count;

    /// <summary>Окно не единственное: можно убрать, и номер имеет смысл.</summary>
    public bool IsMulti => _count > 1;

    /// <summary>«Весь набор в избранное» — только у первого окна и только когда окон несколько.</summary>
    public bool CanSaveSet => IsFirst && IsMulti;

    public string NumberText => $"#{_number}";

    /// <summary>В окне пусто: запуск, очистка и избранное у него без дела.</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(_text);

    private bool _tailEmpty = true;

    /// <summary>Пусто и в этом окне, и во всех ниже: «это и все ниже» без дела. Ставит вкладка.</summary>
    public bool IsTailEmpty
    {
        get => _tailEmpty;
        set => Set(ref _tailEmpty, value);
    }

    public void Place(int number, int count)
    {
        _number = number;
        _count = count;
        RaiseMany(nameof(Number), nameof(IsFirst), nameof(HasBelow), nameof(IsMulti), nameof(CanSaveSet), nameof(NumberText));
    }

    // ── прогон ──────────────────────────────────────────────────────────────

    public StepState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
            {
                RaiseMany(nameof(StatusText), nameof(ClockText), nameof(IsRunning), nameof(HasStatus));
            }
        }
    }

    public bool IsRunning => _state == StepState.Running;

    public bool HasStatus => _state != StepState.Idle;

    public string StatusText => _state switch
    {
        StepState.Waiting => "ждёт",
        StepState.Running => "идёт",
        StepState.Ok => "ок",
        StepState.Failed => "не ок",
        StepState.Skipped => "не запускалось",
        _ => string.Empty,
    };

    /// <summary>Секундомер окна: от первого сервера, начавшего окно, до последнего, закончившего.</summary>
    public string ClockText
    {
        get
        {
            if (_state is StepState.Idle or StepState.Waiting or StepState.Skipped)
            {
                return string.Empty;
            }

            var t = _clock.Elapsed;
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }
    }

    /// <summary>Сколько длилось окно — для итога в выводе.</summary>
    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>Секундомер на экране идёт тиком страницы, а не своим таймером.</summary>
    public void Tick()
    {
        if (_state == StepState.Running)
        {
            Raise(nameof(ClockText));
        }
    }

    /// <summary>Окно не в этом прогоне: прошлый статус стирается, чтобы не путать.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _clock.Reset();
            _pending = 0;
            State = StepState.Idle;
        }
    }

    /// <summary>Окно в прогоне на <paramref name="servers"/> серверах.</summary>
    public void Begin(int servers)
    {
        lock (_gate)
        {
            _clock.Reset();
            _pending = servers;
            _ok = 0;
            _failed = false;
            State = StepState.Waiting;
        }
    }

    /// <summary>
    /// Повтор на серверах, где цепочка встала. Все прошлые ошибки окна — с этих серверов,
    /// поэтому они забываются; прошлые «ок» остаются в счёт.
    /// </summary>
    public void Resume(int servers)
    {
        lock (_gate)
        {
            _clock.Reset();
            _pending = servers;
            _failed = false;
            State = StepState.Waiting;
        }
    }

    public void ServerStarted()
    {
        lock (_gate)
        {
            if (!_clock.IsRunning)
            {
                _clock.Start();
            }

            State = StepState.Running;
        }
    }

    public void ServerFinished(bool ok)
    {
        lock (_gate)
        {
            if (ok)
            {
                _ok++;
            }
            else
            {
                _failed = true;
            }

            Settle();
        }
    }

    /// <summary>Сервер до окна не дошёл — раньше встало.</summary>
    public void ServerSkipped()
    {
        lock (_gate)
        {
            Settle();
        }
    }

    /// <summary>
    /// Прогон закончился. Серверы, которые до окна так и не дошли (отменили ещё до старта),
    /// считаются не дошедшими — иначе окно навсегда осталось бы «ждёт».
    /// </summary>
    public void End()
    {
        lock (_gate)
        {
            if (_pending > 0)
            {
                _pending = 1;
                Settle();
            }
        }
    }

    /// <summary>Вызывать под замком.</summary>
    private void Settle()
    {
        if (--_pending > 0)
        {
            return;
        }

        _clock.Stop();
        State = _failed ? StepState.Failed : _ok > 0 ? StepState.Ok : StepState.Skipped;
    }

    public SavedCommandStep ToSaved() => new() { Text = Text, UseSudo = UseSudo };

    public static CommandStep From(SavedCommandStep saved) => new() { _text = saved.Text ?? string.Empty, _useSudo = saved.UseSudo };
}
