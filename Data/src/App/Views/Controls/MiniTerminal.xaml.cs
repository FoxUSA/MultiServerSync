using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MultiServerSync.Models;
using MultiServerSync.Services;
using MultiServerSync.Views.Pages;

namespace MultiServerSync.Views.Controls;

/// <summary>
/// Плашки в одну строку. Ширину просит не больше, чем дают: StackPanel просил бы все плашки разом,
/// и таблица разъезжалась бы шире своей колонки.
/// </summary>
public sealed class TagRowPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = 0.0;
        var height = 0.0;

        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            width += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(Math.Min(width, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var left = 0.0;

        foreach (UIElement child in InternalChildren)
        {
            child.Arrange(new Rect(left, 0, child.DesiredSize.Width, finalSize.Height));
            left += child.DesiredSize.Width;
        }

        return finalSize;
    }
}

/// <summary>
/// Мини-терминал на «Копировании» и «Скачивании»: самостоятельное окно вкладки, с личной вкладкой команд
/// не связано. Команды идут на серверы, отмеченные слева, вывод — в терминале вкладки.
/// </summary>
public partial class MiniTerminal : UserControl
{
    public static readonly DependencyProperty HasClipboardTextProperty = DependencyProperty.Register(
        nameof(HasClipboardText), typeof(bool), typeof(MiniTerminal), new PropertyMetadata(true));

    /// <summary>В буфере обмена есть текст — значку «Вставить» есть что делать.</summary>
    public bool HasClipboardText
    {
        get => (bool)GetValue(HasClipboardTextProperty);
        private set => SetValue(HasClipboardTextProperty, value);
    }

    public static readonly DependencyProperty TagRowHeightProperty = DependencyProperty.Register(
        nameof(TagRowHeight), typeof(double), typeof(MiniTerminal),
        new PropertyMetadata(double.NaN, (d, e) => ((MiniTerminal)d).TagRow.Height = (double)e.NewValue));

    /// <summary>Высота ряда плашек — как у ряда кнопок слева: окно и параметры начинаются вровень.</summary>
    public double TagRowHeight
    {
        get => (double)GetValue(TagRowHeightProperty);
        set => SetValue(TagRowHeightProperty, value);
    }

    /// <summary>Ниже этого окно не ужимается: значки сверху и sudo снизу налезали бы друг на друга.</summary>
    private const double MinStepHeight = 104;

    /// <summary>Сколько после окончания команды её повтор считается случайным двойным нажатием.</summary>
    private static readonly TimeSpan RepeatGuard = TimeSpan.FromSeconds(3);

    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private readonly TextBlock _moreText = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _more;

    private string? _lastRun;
    private DateTime _lastFinishedAt;
    private bool _repeatConfirmed;

    public MiniTerminal()
    {
        InitializeComponent();

        _moreText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _more = Plate(_moreText);

        StepsScroll.SizeChanged += (_, _) => FitSteps();
        ((INotifyCollectionChanged)StepsList.Items).CollectionChanged += (_, _) => FitSteps();

        Wpf.Ui.Appearance.ApplicationThemeManager.Changed += (_, _) => PaintBoxes();

        _clock.Tick += (_, _) =>
        {
            foreach (var step in Tab?.Steps ?? [])
            {
                step.Tick();
            }
        };

        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _clock.Start();
                BuildTags();
            }
            else
            {
                _clock.Stop();
            }
        };

        Loaded += (_, _) =>
        {
            HasClipboardText = ClipboardWatch.HasText;
            ClipboardWatch.Attach(this);
        };
        ClipboardWatch.Changed += () => HasClipboardText = ClipboardWatch.HasText;

        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged old)
            {
                old.PropertyChanged -= OnTabChanged;
            }

            if (e.NewValue is INotifyPropertyChanged tab)
            {
                tab.PropertyChanged += OnTabChanged;
            }

            BuildTags();
        };
    }

    private Workspace? Tab => DataContext as Workspace;

    private static CommandStep? StepOf(object sender) => (sender as FrameworkElement)?.DataContext as CommandStep;

    // ── серверы над окном ───────────────────────────────────────────────────

    /// <summary>Отметки в панели слева поменялись: сводка на странице пересчитывается вместе с ними.</summary>
    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Workspace.TargetsInfo))
        {
            BuildTags();
        }
    }

    /// <summary>Плашка как у сервера в «Проводнике», только не нажимается.</summary>
    private static Border Plate(UIElement content) => new()
    {
        Margin = new Thickness(0, 0, 4, 0),
        Padding = new Thickness(10, 5, 10, 5),
        Background = new SolidColorBrush(Color.FromArgb(0x26, 0xE0, 0x7A, 0x1F)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0xE0, 0x7A, 0x1F)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Child = content,
    };

    private static Border ServerPlate(string label)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };

        row.Children.Add(new Image
        {
            Width = 16,
            Height = 16,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Source = ShellIcons.Server,
        });

        row.Children.Add(new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Text = label,
        });

        return Plate(row);
    }

    private void BuildTags()
    {
        TagRow.Children.Clear();

        if (Tab is not { } tab)
        {
            return;
        }

        var servers = tab.CopyTargets;

        if (servers.Count == 0)
        {
            TagRow.Children.Add(ServerPlate("Сервер не выбран"));
        }

        foreach (var server in servers)
        {
            var plate = ServerPlate(server.PaneLabel);
            plate.Tag = server.PaneLabel;
            TagRow.Children.Add(plate);
        }

        TagRow.Children.Add(_more);
        FitTags();
    }

    private void OnTagRowSized(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
        {
            FitTags();
        }
    }

    /// <summary>Сколько плашек влезает в строку; остальные — в «ещё N», имена в её подсказке.</summary>
    private void FitTags()
    {
        var plates = TagRow.Children.OfType<Border>().Where(b => !ReferenceEquals(b, _more)).ToList();
        var space = TagRow.ActualWidth;

        foreach (var plate in plates)
        {
            plate.Visibility = Visibility.Visible;
            plate.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }

        _more.Visibility = Visibility.Collapsed;

        if (space <= 0 || plates.Sum(p => p.DesiredSize.Width) <= space)
        {
            return;
        }

        var shown = plates.Count;

        // в размер «ещё N» тоже надо влезть; первая плашка видна всегда
        while (shown > 1)
        {
            shown--;
            _moreText.Text = $"ещё {plates.Count - shown}";
            _more.Visibility = Visibility.Visible;
            _more.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            if (plates.Take(shown).Sum(p => p.DesiredSize.Width) + _more.DesiredSize.Width <= space)
            {
                break;
            }
        }

        foreach (var plate in plates.Skip(shown))
        {
            plate.Visibility = Visibility.Collapsed;
        }

        _more.ToolTip = string.Join(", ", plates.Skip(shown).Select(p => p.Tag as string));
    }

    // ── окна ────────────────────────────────────────────────────────────────

    private void FitSteps() =>
        StepsList.Height = Math.Max(StepsScroll.ActualHeight, StepsList.Items.Count * MinStepHeight);

    private void OnBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Control box)
        {
            PaintBox(box);
        }
    }

    /// <summary>
    /// Фон окна не меняется ни под мышью, ни в фокусе: на большом поле это читается как мигание.
    /// Цветной полоски снизу у фокуса здесь тоже нет.
    /// </summary>
    private void PaintBox(Control box)
    {
        box.Resources["TextControlFocusedBorderBrush"] = Brushes.Transparent;

        if (TryFindResource("TextControlBackground") is not Brush brush)
        {
            return;
        }

        box.Resources["TextControlBackground"] = brush;
        box.Resources["TextControlBackgroundPointerOver"] = brush;
        box.Resources["TextControlBackgroundFocused"] = brush;
    }

    private void PaintBoxes()
    {
        foreach (var step in Tab?.Steps ?? [])
        {
            if (FindBox(step) is { } box)
            {
                PaintBox(box);
            }
        }
    }

    /// <summary>Текст не заходит под значки: правый отступ поля — по ширине их ряда.</summary>
    private void OnIconsResized(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement icons
            && icons.Parent is Grid grid
            && grid.Children.OfType<TextBox>().FirstOrDefault() is { } box)
        {
            var pad = box.Padding;
            box.Padding = new Thickness(pad.Left, pad.Top, icons.ActualWidth + icons.Margin.Right + 12, pad.Bottom);
        }
    }

    private TextBox? FindBox(CommandStep step)
    {
        if (StepsList.ItemContainerGenerator.ContainerFromItem(step) is not ContentPresenter presenter
            || presenter.ContentTemplate is not { } template)
        {
            return null;
        }

        presenter.ApplyTemplate();
        return template.FindName("Box", presenter) as TextBox;
    }

    private void FocusStep(CommandStep step) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (FindBox(step) is { } box)
            {
                box.Focus();
                box.CaretIndex = box.Text.Length;
            }
        }));

    private void Flash(CommandStep step)
    {
        if (StepsList.ItemContainerGenerator.ContainerFromItem(step) is ContentPresenter presenter
            && presenter.ContentTemplate?.FindName("Flash", presenter) is UIElement flash)
        {
            flash.BeginAnimation(OpacityProperty, CommandPage.Blink());
        }
    }

    // ── значки ──────────────────────────────────────────────────────────────

    /// <summary>Вставка заменяет окно целиком, как на «Команде»; вывод терминала из буфера не вставляется.</summary>
    private void OnPaste(object sender, RoutedEventArgs e)
    {
        if (StepOf(sender) is not { } step)
        {
            return;
        }

        string text;

        try
        {
            text = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
        }
        catch (Exception ex)
        {
            Flash(step);
            AppLog.Hint("Не удалось прочитать буфер обмена: " + ex.Message, LogLevel.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            Flash(step);
            AppLog.Hint("В буфере обмена нет текста.", LogLevel.Warning);
            return;
        }

        if (ClipboardText.TerminalOutputWarning(text) is { } warning)
        {
            Toast.ShowInPlace(AppLog.WarnInPlace(warning));
            return;
        }

        step.Text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        ActionLog.Info($"Вставлено из буфера ({step.Text.Length} символов): {CommandPage.OneLine(step.Text)}", null, AppState.Current.Section, Tab, [step.Text.TrimEnd()]);
        FocusStep(step);
    }

    private void OnBoxPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(DataFormats.UnicodeText) is string text
            && ClipboardText.TerminalOutputWarning(text) is { } warning)
        {
            e.CancelCommand();
            Toast.ShowInPlace(AppLog.WarnInPlace(warning));
        }
    }

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        if (StepOf(sender) is { } step)
        {
            await RunAsync(step);
        }
    }

    /// <summary>
    /// Пуск с защитой от двойного нажатия: та же команда сразу после окончания прошлой не выполняется —
    /// посреди окна просьба нажать ещё раз.
    /// </summary>
    private async Task RunAsync(CommandStep step)
    {
        if (Tab is not { } tab)
        {
            return;
        }

        if (step.IsEmpty)
        {
            Flash(step);
            AppLog.Hint("Команда пустая.", LogLevel.Warning);
            return;
        }

        if (tab.CopyTargets.Count == 0)
        {
            if (Window.GetWindow(this) is MainWindow main)
            {
                main.FlashTargets();
            }

            AppLog.Hint("Не отмечено ни одного сервера — отметьте нужные в панели слева.", LogLevel.Warning);
            return;
        }

        var signature = $"{tab.Id}|{step.Number}|{step.UseSudo}|{step.Text}";

        if (AppState.Current.Settings.RepeatGuard
            && !_repeatConfirmed
            && signature == _lastRun
            && DateTime.Now - _lastFinishedAt < RepeatGuard)
        {
            _repeatConfirmed = true;
            Toast.ShowInPlace(AppLog.WarnInPlace(
                "Команда только что выполнена — для повтора нажмите ещё раз.\n"
                + "Отключить: Настройки → Защита от двойного запуска."));
            return;
        }

        _repeatConfirmed = false;

        if (await tab.RunCommandAsync(from: step, onlyThis: true, servers: tab.CopyTargets))
        {
            _lastRun = signature;
            _lastFinishedAt = DateTime.Now;
        }
    }

    private async void OnStop(object sender, RoutedEventArgs e) => await StopAsync();

    private async Task StopAsync()
    {
        if (Tab is { CommandBatch.IsRunning: true } tab)
        {
            await tab.CommandBatch.StopAsync();
        }
    }

    /// <summary>То же, что «Текущий лог» в терминале внизу.</summary>
    private void OnCopyOutput(object sender, RoutedEventArgs e)
    {
        if (Tab?.CommandBatch.HasResults != true)
        {
            if (StepOf(sender) is { } step)
            {
                Flash(step);
            }

            AppLog.Hint("Копировать нечего — команда ещё не выполнялась.", LogLevel.Warning);
            return;
        }

        if (Window.GetWindow(this) is MainWindow main)
        {
            main.Terminal.CopyCurrent();
        }
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (StepOf(sender) is not { } step)
        {
            return;
        }

        if (step.IsEmpty)
        {
            Flash(step);
            AppLog.Hint("Окно и так пустое.", LogLevel.Warning);
            return;
        }

        var was = step.Text.TrimEnd();
        step.Text = string.Empty;
        ActionLog.Info("Поле команды очищено", null, AppState.Current.Section, Tab, ["было:", was]);
    }

    /// <summary>Быстрая ссылка в личные команды, как стрелка на ярлыке; личной вкладки нет — создаётся.</summary>
    private void OnGoPersonal(object sender, RoutedEventArgs e)
    {
        if (Tab is not { IsCopyTab: true } tab)
        {
            return;
        }

        var personal = tab.Personal ?? AppState.Current.CreatePersonalTab(tab);
        AppState.Current.JumpToPersonal(personal);
        AppState.Current.ShowTab(personal);
    }

    /// <summary>Как на «Команде»: Ctrl+Enter запускает, а пока идёт — прерывает; Esc прерывает; Ctrl+Shift+C забирает вывод.</summary>
    private async void OnBoxKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var running = Tab?.CommandBatch.IsRunning == true;

        if (ctrl && e.Key == Key.Enter)
        {
            e.Handled = true;

            if (running)
            {
                await StopAsync();
            }
            else if (StepOf(sender) is { } step)
            {
                await RunAsync(step);
            }
        }
        else if (e.Key == Key.Escape && running)
        {
            e.Handled = true;
            await StopAsync();
        }
        else if (ctrl && shift && e.Key == Key.C)
        {
            e.Handled = true;
            OnCopyOutput(sender, e);
        }
    }
}
