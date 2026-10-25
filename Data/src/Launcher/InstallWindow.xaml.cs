using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace MultiServerSync.Launcher;

public enum StepState
{
    Waiting,
    Running,
    Done,
    Failed,
}

/// <summary>Строка списка шагов — как в окне обновления самой программы.</summary>
public sealed class InstallStep : INotifyPropertyChanged
{
    private StepState _state;

    public InstallStep(string text) => Text = text;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Text { get; }

    public StepState State
    {
        get => _state;
        set
        {
            if (_state == value)
            {
                return;
            }

            _state = value;
            Raise(nameof(State));
            Raise(nameof(Glyph));
            Raise(nameof(GlyphBrush));
            Raise(nameof(Weight));
            Raise(nameof(Opacity));
        }
    }

    public string Glyph => State switch
    {
        StepState.Running => "➜",
        StepState.Done => "✓",
        StepState.Failed => "✗",
        _ => "○",
    };

    public Brush? GlyphBrush => Application.Current.TryFindResource(State switch
    {
        StepState.Done => "SystemFillColorSuccessBrush",
        StepState.Failed => "SystemFillColorCriticalBrush",
        StepState.Running => "AccentTextFillColorPrimaryBrush",
        _ => "TextFillColorTertiaryBrush",
    }) as Brush;

    public FontWeight Weight => State == StepState.Running ? FontWeights.SemiBold : FontWeights.Normal;

    public double Opacity => State == StepState.Waiting ? 0.6 : 1.0;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Окно установки обновления. Продолжает список шагов из окна программы: первые три
/// (скачать, распаковать, проверить) там уже сделаны, здесь — закрытие, замена, уборка, запуск.
/// Сама работа идёт в фоне, окно только показывает, где она сейчас.
/// </summary>
public partial class InstallWindow : FluentWindow
{
    // тексты слово в слово как в окне программы (Views\Dialogs\UpdateWindow.xaml.cs)
    private readonly InstallStep[] _steps =
    {
        new("Скачать архив с byfox.dev"),
        new("Распаковать во временную папку Update рядом с программой"),
        new("Проверить содержимое архива"),
        new("Закрыть программу"),
        new("Удалить старые Data и MultiServerSync.exe (UserData не трогается)"),
        new("Поставить новую версию"),
        new("Удалить временную папку Update"),
        new("Запустить программу заново"),
    };

    private readonly Func<InstallWindow, bool> _work;
    private volatile bool _working = true;

    /// <param name="work">Установка; true — всё встало. Вызывается в фоновом потоке.</param>
    public InstallWindow(Func<InstallWindow, bool> work)
    {
        InitializeComponent();

        _work = work;
        StepList.ItemsSource = _steps;

        // первые три шага сделала программа до своего закрытия
        Step(3);

        ContentRendered += (_, _) => Task.Run(Run);

        // закрыть посреди замены файлов нельзя: программа осталась бы наполовину старой
        Closing += (_, e) => e.Cancel = _working;
    }

    /// <summary>Чем всё кончилось: окно закрыли после успеха или после сбоя.</summary>
    public bool Succeeded { get; private set; }

    private void Run()
    {
        bool ok;

        try
        {
            ok = _work(this);
        }
        catch (Exception ex)
        {
            Fail("Обновление не установилось: " + ex.Message);
            ok = false;
        }

        _working = false;

        if (ok)
        {
            Succeeded = true;
            Ui(Close);
        }
    }

    /// <summary>Отметить текущий шаг: всё до него — сделано, он — идёт.</summary>
    public void Step(int index, string? status = null) => Ui(() =>
    {
        for (var i = 0; i < _steps.Length; i++)
        {
            _steps[i].State = i < index ? StepState.Done : i == index ? StepState.Running : StepState.Waiting;
        }

        if (status is not null)
        {
            StageText.Text = status;
        }

        // полоса — общий ход по шагам, пока шаг сам не сообщит процент
        Bar.IsIndeterminate = false;
        Bar.Value = index * 100.0 / _steps.Length;
    });

    /// <summary>Подробности внутри шага: текст и, если известно, процент для полосы.</summary>
    public void Progress(string status, double? percent = null) => Ui(() =>
    {
        StageText.Text = status;

        if (percent is { } value)
        {
            Bar.IsIndeterminate = false;
            Bar.Value = value;
        }
        else
        {
            Bar.IsIndeterminate = true;
        }
    });

    /// <summary>Сбой: шаг, на котором остановились, — красным, причина — словами, кнопка запуска.</summary>
    public void Fail(string reason) => Ui(() =>
    {
        foreach (var step in _steps)
        {
            if (step.State == StepState.Running)
            {
                step.State = StepState.Failed;
            }
        }

        StageText.Text = reason;
        StageText.Foreground = Application.Current.TryFindResource("SystemFillColorCriticalBrush") as Brush
            ?? StageText.Foreground;
        Bar.IsIndeterminate = false;
        Bar.Value = 0;
        RunButton.Visibility = Visibility.Visible;
        RunButton.Focus();
    });

    private void OnRun(object sender, RoutedEventArgs e)
    {
        if (!_working)
        {
            Close();
        }
    }

    private void Ui(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.BeginInvoke(action);
        }
    }
}
