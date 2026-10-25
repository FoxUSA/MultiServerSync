using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using MultiServerSync.Core;
using MultiServerSync.Services;
using Wpf.Ui.Controls;

namespace MultiServerSync.Views.Dialogs;

public enum StepState
{
    Waiting,
    Running,
    Done,
    Failed,
}

/// <summary>Строка списка шагов: значок состояния слева, текст справа.</summary>
public sealed class UpdateStep : ObservableObject
{
    private StepState _state;

    public UpdateStep(string text) => Text = text;

    public string Text { get; }

    public StepState State
    {
        get => _state;
        set
        {
            if (Set(ref _state, value))
            {
                RaiseMany(nameof(Glyph), nameof(GlyphBrush), nameof(Weight), nameof(Opacity));
            }
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
}

/// <summary>
/// Обновление целиком в одном окне: проверка, загрузка, распаковка во временную папку,
/// проверка архива — здесь; закрытие программы, замена Data и лаунчера, уборка
/// временной папки и перезапуск — в окне лаунчера, с тем же списком шагов.
/// </summary>
public partial class UpdateWindow : FluentWindow
{
    // шаги 0–2 делает программа, 3–7 — лаунчер после её закрытия
    private readonly UpdateStep[] _steps =
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

    private CancellationTokenSource? _cts;
    private bool _busy;

    public UpdateWindow()
    {
        InitializeComponent();

        StepList.ItemsSource = _steps;
        Closing += OnClosingWindow;

        ShowRelease();

        // кнопка в шапке появляется только после проверки, но окно можно открыть
        // и из настроек — тогда сначала спрашиваем сайт
        if (UpdateService.Available is null)
        {
            Loaded += async (_, _) => await CheckAsync();
        }
    }

    /// <summary>Открыть окно поверх главного.</summary>
    public static void Open(Window? owner)
    {
        var window = new UpdateWindow { Owner = owner };
        window.ShowDialog();
    }

    private void ShowRelease()
    {
        var available = UpdateService.Available;

        if (available is null)
        {
            HeadText.Text = "Проверка обновлений";
            SubText.Text = $"Установлена версия {AppVersion.Details}.";
            UpdateButton.IsEnabled = false;
            return;
        }

        HeadText.Text = $"Доступна версия {available.Caption}";
        SubText.Text = $"Установлена {AppVersion.Details}. "
            + "Программа закроется, обновится и откроется заново. Серверы, вкладки и настройки останутся.";
        UpdateButton.IsEnabled = !_busy;
        UpdateButton.Content = $"Обновить до {available.Version}";
    }

    private async Task CheckAsync()
    {
        SetBusy(true);

        try
        {
            await UpdateService.CheckAsync(new Progress<UpdateProgress>(Show));
        }
        catch (Exception ex)
        {
            Show(new UpdateProgress(UpdateStage.Failed, "Проверить не удалось: " + ex.Message));
        }
        finally
        {
            SetBusy(false);
            ShowRelease();
        }
    }

    private async void OnUpdate(object sender, RoutedEventArgs e)
    {
        if (_busy || UpdateService.Available is not { } release)
        {
            return;
        }

        foreach (var step in _steps)
        {
            step.State = StepState.Waiting;
        }

        _cts = new CancellationTokenSource();
        SetBusy(true);

        try
        {
            await UpdateService.DownloadAndStageAsync(release, new Progress<UpdateProgress>(Show), _cts.Token);

            // дальше программа закрывается: пусть человек успеет увидеть, что всё готово
            Mark(3);
            Show(new UpdateProgress(
                UpdateStage.Ready, "Всё скачано и проверено. Закрываем программу — установку продолжит лаунчер.", 100));

            await Task.Delay(900);

            // отменять уже нечего: окно должно закрыться вместе с программой
            _cts.Dispose();
            _cts = null;

            UpdateService.ApplyAndRestart();
        }
        catch (OperationCanceledException)
        {
            Fail("Обновление отменено. Временная папка удалена, программа не менялась.");
            UpdateService.ClearTemp();
            AppLog.Info("Обновление отменено.");
        }
        catch (Exception ex)
        {
            Fail("Не получилось: " + ex.Message + " Программа не менялась.");
            UpdateService.ClearTemp();
            AppLog.Error("Обновление не удалось: " + ex.Message);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    /// <summary>Отметить текущий шаг: всё до него — сделано, он — идёт.</summary>
    private void Mark(int index)
    {
        for (var i = 0; i < _steps.Length; i++)
        {
            _steps[i].State = i < index ? StepState.Done : i == index ? StepState.Running : StepState.Waiting;
        }
    }

    private void Fail(string text)
    {
        foreach (var step in _steps)
        {
            if (step.State == StepState.Running)
            {
                step.State = StepState.Failed;
            }
        }

        Show(new UpdateProgress(UpdateStage.Failed, text));
    }

    private void Show(UpdateProgress progress)
    {
        switch (progress.Stage)
        {
            case UpdateStage.Downloading:
                Mark(0);
                break;
            case UpdateStage.Unpacking:
                Mark(1);
                break;
            case UpdateStage.Staging:
                Mark(2);
                break;
        }

        StageText.Text = progress.Text;

        if (progress.Percent is { } percent)
        {
            Bar.IsIndeterminate = false;
            Bar.Value = percent;
        }
        else
        {
            // шаг без процентов: полоса просто показывает, что работа идёт
            Bar.IsIndeterminate = progress.Stage is UpdateStage.Checking or UpdateStage.Staging;
            Bar.Value = 0;
        }

        if (progress.Stage is UpdateStage.UpToDate or UpdateStage.Failed or UpdateStage.Ready)
        {
            Bar.IsIndeterminate = false;
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateButton.IsEnabled = !busy && UpdateService.Available is not null;
        CloseButton.Content = busy && _cts is not null ? "Отмена" : "Закрыть";
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            return;
        }

        Close();
    }

    /// <summary>Крестик окна посреди загрузки — это отмена, а не брошенная на полпути загрузка.</summary>
    private void OnClosingWindow(object? sender, CancelEventArgs e)
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            e.Cancel = true;
        }
    }
}
