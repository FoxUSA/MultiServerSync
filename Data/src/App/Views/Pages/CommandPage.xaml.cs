using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MultiServerSync.Models;
using MultiServerSync.Services;
using MultiServerSync.Views.Dialogs;

namespace MultiServerSync.Views.Pages;

/// <summary>
/// Страница команды. Окна команды и их sudo принадлежат текущей вкладке, история
/// и избранные команды общие для всех вкладок. Окон может быть несколько — они идут
/// цепочкой, у каждого свой запуск, номер, секундомер и статус.
/// </summary>
public partial class CommandPage : Page
{
    /// <summary>Кнопке сейчас нечего делать: она тусклая, но жмётся — нажатие мигает рамкой окна.</summary>
    public static readonly DependencyProperty IdleProperty = DependencyProperty.RegisterAttached(
        "Idle", typeof(bool), typeof(CommandPage), new PropertyMetadata(false));

    public static bool GetIdle(DependencyObject element) => (bool)element.GetValue(IdleProperty);

    public static void SetIdle(DependencyObject element, bool value) => element.SetValue(IdleProperty, value);

    public static readonly DependencyProperty HasClipboardTextProperty = DependencyProperty.Register(
        nameof(HasClipboardText), typeof(bool), typeof(CommandPage), new PropertyMetadata(true));

    /// <summary>В буфере обмена есть текст — значку «Вставить» есть что делать.</summary>
    public bool HasClipboardText
    {
        get => (bool)GetValue(HasClipboardTextProperty);
        private set => SetValue(HasClipboardTextProperty, value);
    }

    /// <summary>Тик секундомеров окон — один на страницу, а не по таймеру на окно.</summary>
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public CommandPage()
    {
        InitializeComponent();

        // история — своя у вкладки, та же, что в избранном: строка привязана к CommandHistory открытой вкладки
        DataContext = AppState.Current.ActiveWorkspace;
        AppState.Current.ActiveWorkspaceChanged += workspace => DataContext = workspace;

        // выбор в истории держится только пока окно совпадает с выбранной строкой
        DataContextChanged += OnWorkspaceSwitched;
        Attach(Workspace);

        Loaded += (_, _) => ListenClipboard();
        ClipboardWatch.Changed += () => HasClipboardText = ClipboardWatch.HasText;

        StepsScroll.SizeChanged += (_, _) => FitSteps();
        ((INotifyCollectionChanged)StepsList.Items).CollectionChanged += (_, _) => FitSteps();

        Wpf.Ui.Appearance.ApplicationThemeManager.Changed += (_, _) => PaintBoxes();

        _clock.Tick += (_, _) =>
        {
            foreach (var step in Workspace?.Steps ?? [])
            {
                step.Tick();
            }
        };

        // ярлыки вкладок пишут статистику по открытому разделу
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                AppState.Current.Section = AppSection.Command;
                _clock.Start();
            }
            else
            {
                _clock.Stop();
            }
        };
    }

    private Workspace? Workspace => DataContext as Workspace;

    /// <summary>Окно, куда подставлять: окно в фокусе.</summary>
    private CommandStep? ActiveStep => Workspace?.Steps.FirstOrDefault(s => s.IsFocused);

    private static CommandStep? StepOf(object sender) => (sender as FrameworkElement)?.DataContext as CommandStep;

    // ── окна ────────────────────────────────────────────────────────────────

    /// <summary>Ниже этого окно не ужимается: два ряда значков сверху и sudo снизу налезали друг на друга.</summary>
    private const double MinStepHeight = 130;

    /// <summary>Окна делят видимую высоту поровну, а когда им тесно — уходят под прокрутку.</summary>
    private void FitSteps() =>
        StepsList.Height = Math.Max(StepsScroll.ActualHeight, StepsList.Items.Count * MinStepHeight);

    private void OnStepBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Control box)
        {
            PaintBox(box);
        }
    }

    /// <summary>Текст не заходит под значки: правый отступ поля — по ширине их столбика, она меняется с кнопками.</summary>
    private void OnStepIconsResized(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement icons
            && icons.Parent is Grid grid
            && grid.Children.OfType<TextBox>().FirstOrDefault() is { } box)
        {
            var pad = box.Padding;
            box.Padding = new Thickness(pad.Left, pad.Top, icons.ActualWidth + icons.Margin.Right + 12, pad.Bottom);
        }
    }

    private void OnStepFocused(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (StepOf(sender) is { } step)
        {
            Workspace?.FocusStep(step);
            SyncPickers();
        }
    }

    /// <summary>
    /// Фон окна не меняется ни под мышью, ни в фокусе: тема подменяет его на своих состояниях,
    /// и на окне в полстраницы это читается как мигание.
    /// </summary>
    private void PaintBox(Control box)
    {
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
        foreach (var step in Workspace?.Steps ?? [])
        {
            if (FindBox(step) is { } box)
            {
                PaintBox(box);
            }
        }
    }

    /// <summary>Поле ввода окна по самому окну — для фокуса и раскраски.</summary>
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

    private void OnAddStep(object sender, RoutedEventArgs e)
    {
        if (Workspace is not { } workspace)
        {
            return;
        }

        var step = workspace.AddStep(StepOf(sender));
        ActionLog.Info($"Добавлено окно команды #{step.Number} · окон {workspace.Steps.Count}", null, AppSection.Command, workspace);
        FocusStep(step);
    }

    private void OnRemoveStep(object sender, RoutedEventArgs e)
    {
        if (Workspace is not { } workspace || StepOf(sender) is not { } step)
        {
            return;
        }

        var number = step.Number;
        var text = step.Text;

        if (workspace.RemoveStep(step))
        {
            ActionLog.Info($"Убрано окно команды #{number} · окон {workspace.Steps.Count}", null, AppSection.Command, workspace,
                string.IsNullOrWhiteSpace(text) ? ["окно было пустым"] : ["в нём было:", text.TrimEnd()]);
        }
    }

    private void OnClearStep(object sender, RoutedEventArgs e)
    {
        if (StepOf(sender) is not { } step)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(step.Text))
        {
            FlashSteps([step]);
            AppLog.Hint("Окно и так пустое.", LogLevel.Warning);
            return;
        }

        var was = step.Text.TrimEnd();
        step.Text = string.Empty;
        ActionLog.Info(Workspace?.Steps.Count > 1 ? $"Очищено окно команды #{step.Number}" : "Поле команды очищено", null, AppSection.Command, Workspace,
            ["было:", was]);
        HistoryBox.SelectedItem = null;
    }

    // ── переключение вкладки ────────────────────────────────────────────────

    private void OnWorkspaceSwitched(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is Workspace old)
        {
            old.PropertyChanged -= OnWorkspaceProperty;
        }

        Attach(e.NewValue as Workspace);
    }

    private void Attach(Workspace? workspace)
    {
        if (workspace is not null)
        {
            workspace.PropertyChanged += OnWorkspaceProperty;
        }

        SyncPickers();

        // у другой вкладки своя сессия (или её нет) — сторона окна без переворота
        ApplyFace();
    }

    private void OnWorkspaceProperty(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Services.Workspace.CommandText))
        {
            SyncPickers();
        }
        else if (e.PropertyName == nameof(Services.Workspace.HasSession))
        {
            // ввод сессии открывается и закрывается в фоне — переворот в потоке окна
            Dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(sender, Workspace))
                {
                    Flip(Workspace!.HasSession);
                }
            });
        }
    }

    // ── окно ответа сессии на обороте окон команды («PowerShell») ────────────────

    /// <summary>Половина переворота — как у окна вывода в «Проводнике».</summary>
    private static readonly Duration FlipHalf = new(TimeSpan.FromMilliseconds(130));

    private bool _sessionShown;

    private bool _flipping;

    /// <summary>Переворот как у страницы: окна сжимаются к середине и разворачиваются другой стороной.</summary>
    private void Flip(bool session)
    {
        if (session == _sessionShown)
        {
            return;
        }

        if (_flipping || !IsVisible)
        {
            ApplyFace();
            return;
        }

        _flipping = true;
        _sessionShown = session;

        var fold = new DoubleAnimation(1, 0, FlipHalf) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseIn } };
        fold.Completed += (_, _) =>
        {
            ShowFace(session);

            var unfold = new DoubleAnimation(0, 1, FlipHalf) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut } };
            unfold.Completed += (_, _) =>
            {
                FlipScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                _flipping = false;

                // пока крутилось, сессия могла кончиться или начаться снова
                if (Workspace is { } now && now.HasSession != _sessionShown)
                {
                    Flip(now.HasSession);
                }
                else if (session)
                {
                    SessionBox.Focus();
                }
            };
            FlipScale.BeginAnimation(ScaleTransform.ScaleXProperty, unfold);
        };
        FlipScale.BeginAnimation(ScaleTransform.ScaleXProperty, fold);
    }

    /// <summary>Сторона без переворота — при смене вкладки.</summary>
    private void ApplyFace()
    {
        FlipScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _flipping = false;
        _sessionShown = Workspace?.HasSession == true;
        ShowFace(_sessionShown);
    }

    private void ShowFace(bool session)
    {
        SessionFace.Visibility = session ? Visibility.Visible : Visibility.Collapsed;
        KeepSessionCommand.IsChecked = AppState.Current.Settings.SessionKeepCommand;
        StepsScroll.Visibility = session ? Visibility.Collapsed : Visibility.Visible;

        // черновик ответа — свой у вкладки
        _loadingDraft = true;
        SessionBox.Text = Workspace?.SessionDraft ?? string.Empty;
        _loadingDraft = false;
    }

    private bool _loadingDraft;

    private void OnSessionTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loadingDraft && Workspace is { } workspace)
        {
            workspace.SessionDraft = SessionBox.Text;
        }
    }

    /// <summary>Enter — отправить, Shift+Enter — новая строка, Ctrl+C без выделения — прервать в сессии, как в терминале.</summary>
    private void OnSessionKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.C
            && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control
            && SessionBox.SelectionLength == 0)
        {
            e.Handled = true;
            OnInterruptSession(sender, e);
            return;
        }

        if (e.Key == System.Windows.Input.Key.Enter
            && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            SendSession();
        }
    }

    private void OnSendSession(object sender, RoutedEventArgs e) => SendSession();

    /// <summary>«Сохранять команду» — одна на программу, по умолчанию снята.</summary>
    private void OnKeepSessionCommand(object sender, RoutedEventArgs e)
    {
        AppState.Current.Settings.SessionKeepCommand = KeepSessionCommand.IsChecked == true;
        AppState.Current.SaveSettings();
        SessionBox.Focus();
    }

    private void SendSession()
    {
        if (Workspace is not { } workspace)
        {
            return;
        }

        if (workspace.SendToSession(SessionBox.Text) is { } refused)
        {
            AppLog.Hint(refused, LogLevel.Warning);
            return;
        }

        // «Сохранять команду»: набранное остаётся — поправить и отправить снова; без неё окно очищается
        if (!AppState.Current.Settings.SessionKeepCommand)
        {
            SessionBox.Clear();
        }

        SessionBox.Focus();
    }

    /// <summary>Вставка из буфера заменяет окно ответа целиком — как у окна команды.</summary>
    private void OnPasteSession(object sender, RoutedEventArgs e)
    {
        string text;

        try
        {
            text = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
        }
        catch (Exception ex)
        {
            AppLog.Hint("Не удалось прочитать буфер обмена: " + ex.Message, LogLevel.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            AppLog.Hint("В буфере обмена нет текста.", LogLevel.Warning);
            return;
        }

        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        SessionBox.Text = text;
        SessionBox.CaretIndex = text.Length;
        SessionBox.Focus();
    }

    private void OnClearSession(object sender, RoutedEventArgs e)
    {
        SessionBox.Clear();
        SessionBox.Focus();
    }

    /// <summary>♡ у окна ответа: команда из избранного встаёт в него; запись из нескольких окон — строками подряд.</summary>
    private void OnOpenBookSession(object sender, RoutedEventArgs e)
    {
        if (Workspace is not { } workspace)
        {
            return;
        }

        var window = new SavedCommandStep { Text = SessionBox.Text.Trim() };
        var request = new CommandBookRequest
        {
            Section = AppSection.Command,
            Tab = workspace,
            Window = window,
            AllWindows = [window],
        };

        if (CommandBookDialog.Ask(Window.GetWindow(this), request) is not { } snippet)
        {
            return;
        }

        var text = snippet.IsMulti
            ? string.Join("\n", snippet.AsSteps().Select(s => s.Text.TrimEnd()))
            : snippet.Command;

        SessionBox.Text = text;
        SessionBox.CaretIndex = text.Length;
        SessionBox.Focus();
        AppLog.Echo($"Подставлена команда {CommandBookDialog.Origin(snippet)}");
    }

    private void OnInterruptSession(object sender, RoutedEventArgs e)
    {
        if (Workspace?.InterruptSession() is { } refused)
        {
            AppLog.Hint(refused, LogLevel.Warning);
        }

        SessionBox.Focus();
    }

    /// <summary>
    /// Снимает выбор в истории, как только окно перестало совпадать с выбранной строкой.
    /// Список сообщает о выборе только когда тот сменился: пока в нём висит прошлая запись,
    /// повторный выбор той же команды ничего не подставлял.
    /// </summary>
    private void SyncPickers()
    {
        if (Workspace is null)
        {
            return;
        }

        var text = ActiveStep?.Text ?? string.Empty;

        if (HistoryBox.SelectedItem is string command
            && !string.Equals(command, text, StringComparison.Ordinal))
        {
            HistoryBox.SelectedItem = null;
        }
    }

    // ── история и вставка ───────────────────────────────────────────────────

    private void OnHistoryPicked(object sender, SelectionChangedEventArgs e)
    {
        // сессия открыта — команда из истории встаёт в окно ответа, а не в окно команды
        if (e.AddedItems.Count > 0 && HistoryBox.SelectedItem is string picked && _sessionShown)
        {
            SessionBox.Text = picked;
            SessionBox.CaretIndex = picked.Length;
            SessionBox.Focus();
            HistoryBox.SelectedItem = null;
            return;
        }

        if (e.AddedItems.Count > 0 && HistoryBox.SelectedItem is string text && ActiveStep is { } step)
        {
            step.Text = text;
            AppLog.Echo("Подставлена команда из истории: " + OneLine(text));
        }
    }

    /// <summary>
    /// Вставка из буфера заменяет содержимое окна целиком, а не дописывается в конец:
    /// иначе новый скрипт склеивается с прошлой командой без перевода строки.
    /// </summary>
    private void OnPasteStep(object sender, RoutedEventArgs e)
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
            FlashSteps([step]);
            AppLog.Hint("Не удалось прочитать буфер обмена: " + ex.Message, LogLevel.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            FlashSteps([step]);
            AppLog.Hint("В буфере обмена нет текста.", LogLevel.Warning);
            return;
        }

        if (ClipboardText.TerminalOutputWarning(text) is { } warning)
        {
            PasteToast.ShowInPlace(AppLog.WarnInPlace(warning));
            return;
        }

        step.Text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        AppLog.Echo($"Вставлено из буфера ({step.Text.Length} символов): {OneLine(step.Text)}");

        Workspace?.FocusStep(step);
        FocusStep(step);
    }

    /// <summary>
    /// Вставка в окно с клавиатуры или из меню: вывод терминала, который сами же
    /// положили в буфер (часто — галочкой «после команды»), не вставляем, а предупреждаем.
    /// </summary>
    private void OnCommandBoxPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(DataFormats.UnicodeText) is string text
            && ClipboardText.TerminalOutputWarning(text) is { } warning)
        {
            e.CancelCommand();
            PasteToast.ShowInPlace(AppLog.WarnInPlace(warning));
        }
    }

    /// <summary>Первая строка команды и не длиннее сотни знаков — для журнала и плашки.</summary>
    internal static string OneLine(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var first = lines[0].Trim();

        if (first.Length > 100)
        {
            first = first[..100] + "…";
        }

        return lines.Length > 1 ? $"{first} (+ ещё строк: {lines.Length - 1})" : first;
    }

    // ── избранное ───────────────────────────────────────────────────────────

    /// <summary>
    /// ♡ у окна: избранное. Одиночная запись встаёт в это окно (sudo окна остаётся), запись из нескольких
    /// окон заменяет все окна вкладки. Добавить в избранное это окно или все окна — там же.
    /// </summary>
    private void OnOpenBook(object sender, RoutedEventArgs e)
    {
        if (StepOf(sender) is not { } step || Workspace is not { } workspace)
        {
            return;
        }

        var request = new CommandBookRequest
        {
            Section = AppSection.Command,
            Tab = workspace,
            Window = step.ToSaved(),
            AllWindows = workspace.Steps
                .Where(s => !s.IsEmpty)
                .Select(s => new SavedCommandStep { Text = s.Text.Trim(), UseSudo = s.UseSudo })
                .ToList(),
            AllowMulti = true,
        };

        if (CommandBookDialog.Ask(Window.GetWindow(this), request) is not { } snippet)
        {
            return;
        }

        if (snippet.IsMulti)
        {
            workspace.SetSteps(snippet.AsSteps());
            AppLog.Echo($"Из избранного раскрыто окон: {workspace.Steps.Count} — {snippet.Comment}");
            step = workspace.Steps[0];
        }
        else
        {
            step.Text = snippet.Command;
            AppLog.Echo($"Подставлена команда {CommandBookDialog.Origin(snippet)}");
        }

        workspace.FocusStep(step);
        FocusStep(step);
    }

    /// <summary>♡ у строки истории: избранное открывается сразу новой записью с этой командой.</summary>
    private void OnBookHistoryItem(object sender, RoutedEventArgs e)
    {
        // иначе щелчок дойдёт до строки списка и она будет выбрана
        e.Handled = true;

        if (sender is not FrameworkElement { DataContext: string value } || Workspace is not { } workspace)
        {
            return;
        }

        HistoryBox.IsDropDownOpen = false;
        var step = ActiveStep ?? workspace.Steps.FirstOrDefault();

        var request = new CommandBookRequest
        {
            Section = AppSection.Command,
            Tab = workspace,
            Window = step?.ToSaved() ?? new SavedCommandStep(),
            AddCommand = value,
            AllowMulti = true,
        };

        // после добавления из избранного могут и выбрать запись — она встаёт в окно, как по ♡ у окна
        if (CommandBookDialog.Ask(Window.GetWindow(this), request) is not { } snippet || step is null)
        {
            return;
        }

        if (snippet.IsMulti)
        {
            workspace.SetSteps(snippet.AsSteps());
            step = workspace.Steps[0];
        }
        else
        {
            step.Text = snippet.Command;
        }

        AppLog.Echo($"Подставлена команда {CommandBookDialog.Origin(snippet)}");
        workspace.FocusStep(step);
        FocusStep(step);
    }

    /// <summary>Скопировать команду из истории в буфер.</summary>
    private void OnCopyHistoryItem(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is FrameworkElement { DataContext: string value } && ClipboardText.Copy(value))
        {
            HistoryBox.IsDropDownOpen = false;
            AppLog.Echo("Скопирована команда из истории: " + OneLine(value));
        }
    }

    /// <summary>Крестик в выпадающем списке: убирает одну команду из истории вкладки — как крестик в избранном.</summary>
    private void OnRemoveHistoryItem(object sender, RoutedEventArgs e)
    {
        // иначе щелчок дойдёт до строки списка и она будет выбрана
        e.Handled = true;

        if (sender is not FrameworkElement { DataContext: string value } || Workspace is not { } workspace)
        {
            return;
        }

        workspace.ForgetCommand(value, AppSection.Command);
        HistoryBox.IsDropDownOpen = workspace.CommandHistory.Count > 0;
    }

    // ── запуск ──────────────────────────────────────────────────────────────

    /// <summary>
    /// «Выполнить»: все окна вкладки по очереди на её отмеченных серверах; со свитчем «Везде» —
    /// все командные вкладки. ▶ и Ctrl+Enter у окна — всегда только эта вкладка.
    /// </summary>
    private async void OnRun(object sender, RoutedEventArgs e)
    {
        if (Workspace is not { } workspace)
        {
            return;
        }

        if (AppState.Current.Everywhere)
        {
            await RunEverywhereAsync(workspace);
        }
        else
        {
            await RunGuardedAsync(workspace, null, onlyThis: false);
        }
    }

    /// <summary>▶ у окна: только это окно.</summary>
    private async void OnRunStep(object sender, RoutedEventArgs e)
    {
        if (Workspace is { } workspace && StepOf(sender) is { } step)
        {
            await RunGuardedAsync(workspace, step, onlyThis: true);
        }
    }

    /// <summary>▶▶ у окна: это окно и все ниже.</summary>
    private async void OnRunFromStep(object sender, RoutedEventArgs e)
    {
        if (Workspace is { } workspace && StepOf(sender) is { } step)
        {
            await RunGuardedAsync(workspace, step, onlyThis: false);
        }
    }

    /// <summary>
    /// «Выполнить» со свитчем «Везде» — команды запускаются во всех командных вкладках сразу, каждая
    /// со своими окнами и серверами. Личные вкладки копирования не запускаются: у них свой момент —
    /// после удачного копирования. Сводка показывается на вкладке, с которой нажали.
    /// </summary>
    private async Task RunEverywhereAsync(Workspace workspace)
    {
        // «PowerShell» и серверы — разные ряды вкладок: «везде» — это все вкладки открытого ряда
        var all = AppState.Current.CommandTabs.Where(t => !t.IsPersonal && t.LocalPc == workspace.LocalPc).ToList();

        // широкое действие: команды уходят со всех вкладок сразу — сначала спросить
        if (!ConfirmDialog.Ask(
                Window.GetWindow(this),
                $"Запустить команды на всех вкладках ({all.Count})?",
                string.Empty,
                "Выполнить везде"))
        {
            AppLog.Hint("Запуск на всех вкладках отменён.");
            return;
        }

        // умный режим — одним окном на все вкладки, а не по окну на каждую
        var checks = all.Select(t => (Tab: t, Check: t.PrecheckCommand())).ToList();

        if (RunHistory.ConfirmMany(checks) is not { } chosen)
        {
            return;
        }

        foreach (var (tab, check) in checks.Where(c => chosen.Contains(c.Tab)))
        {
            RunHistory.Commit(check);
        }

        if (chosen.Count == 0)
        {
            AppLog.Info("Запуск на всех вкладках: запускать нечего — все вкладки пропущены.");
            return;
        }

        if (chosen.Count == 1)
        {
            await chosen[0].RunCommandAsync(approved: true);
            return;
        }

        AppLog.Info($"Везде: команды запускаются во всех вкладках: {chosen.Count}.");

        // операции стартуют синхронно, поэтому строки уже созданы и попадут в сводку
        var running = chosen.Select(w => w.RunCommandAsync(notify: false, approved: true)).ToList();

        // вкладка, которая отказалась (нет команды или серверов), в сводку не идёт:
        // её отказ уже виден на ярлыке и в журнале
        var started = chosen.Where((_, i) => !(running[i].IsCompletedSuccessfully && !running[i].Result)).ToList();

        if (started.Count == 0)
        {
            AppLog.Warn("Команда не запущена ни на одной вкладке — причины выше.");
            await Task.WhenAll(running);
            return;
        }

        // сводка только в терминале: на ярлыке первой вкладки — её собственная работа
        workspace.ShowSummary(started.Select(w => w.CommandBatch).ToList(), "команда");

        await Task.WhenAll(running);

        // автокопия — по сводке, когда закончили все вкладки, а не по первой закончившей
        AppState.Current.NotifyFinished(workspace.SummaryBatch);
    }

    /// <summary>Сколько после окончания команды её повтор считается случайным двойным нажатием.</summary>
    private static readonly TimeSpan RepeatGuard = TimeSpan.FromSeconds(3);

    private Workspace? _lastTab;
    private string? _lastCommand;
    private DateTime _lastFinishedAt;
    private bool _repeatConfirmed;

    /// <summary>
    /// Запуск с защитой от двойного нажатия: те же команды сразу после окончания прошлых
    /// не выполняются — посреди окон просьба нажать ещё раз. Повторное нажатие запускает.
    /// </summary>
    private async Task RunGuardedAsync(Workspace workspace, CommandStep? from, bool onlyThis)
    {
        // делать нечего — мигнуть тем, что надо заполнить: пустыми окнами или серверами слева.
        // Запуск всё равно идёт дальше: он сам откажет и объяснит причину
        var steps = from is null ? workspace.Steps.ToList()
            : onlyThis ? [from]
            : workspace.Steps.Skip(Math.Max(0, workspace.Steps.IndexOf(from))).ToList();

        if (steps.All(s => s.IsEmpty))
        {
            FlashSteps(steps);
        }
        else if (workspace.CommandTargets.Count == 0 && Window.GetWindow(this) is MainWindow main)
        {
            main.FlashTargets();
        }

        var signature = $"{from?.Number ?? 0}|{onlyThis}|{workspace.StepsSignature}";

        if (AppState.Current.Settings.RepeatGuard
            && !_repeatConfirmed
            && ReferenceEquals(workspace, _lastTab)
            && signature == _lastCommand
            && DateTime.Now - _lastFinishedAt < RepeatGuard)
        {
            _repeatConfirmed = true;
            PasteToast.ShowInPlace(AppLog.WarnInPlace(
                "Команда только что выполнена — для повтора нажмите ещё раз.\n"
                + "Отключить: Настройки → Защита от двойного запуска."));
            return;
        }

        _repeatConfirmed = false;

        if (await workspace.RunCommandAsync(from: from, onlyThis: onlyThis))
        {
            _lastTab = workspace;
            _lastCommand = signature;
            _lastFinishedAt = DateTime.Now;
        }
    }

    /// <summary>■ над окном — та же остановка, что у кнопки «Остановить» под выводом.</summary>
    private async void OnInterrupt(object sender, RoutedEventArgs e) => await InterruptAsync();

    private async Task InterruptAsync()
    {
        if (Workspace is { CommandBatch.IsRunning: true } workspace)
        {
            await workspace.CommandBatch.StopAsync();
        }
    }

    /// <summary>
    /// Пульт с клавиатуры, не отрывая рук от окна: Ctrl+Enter запускает это окно и все ниже,
    /// а пока команда идёт — прерывает; Esc тоже прерывает; Ctrl+Shift+C забирает вывод.
    /// Обычный Enter — перевод строки.
    /// </summary>
    private async void OnCommandKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var mods = System.Windows.Input.Keyboard.Modifiers;
        var ctrl = (mods & System.Windows.Input.ModifierKeys.Control) != 0;
        var shift = (mods & System.Windows.Input.ModifierKeys.Shift) != 0;
        var running = Workspace?.CommandBatch.IsRunning == true;

        if (ctrl && e.Key == System.Windows.Input.Key.Enter)
        {
            e.Handled = true;

            if (running)
            {
                await InterruptAsync();
            }
            else if (Workspace is { } workspace)
            {
                await RunGuardedAsync(workspace, StepOf(sender), onlyThis: false);
            }
        }
        else if (e.Key == System.Windows.Input.Key.Escape && running)
        {
            e.Handled = true;
            await InterruptAsync();
        }
        else if (ctrl && shift && e.Key == System.Windows.Input.Key.C)
        {
            e.Handled = true;
            OnCopyOutput(sender, e);
        }
    }

    /// <summary>Значок над первым окном: то же, что «Текущий лог» в терминале внизу.</summary>
    private void OnCopyOutput(object sender, RoutedEventArgs e)
    {
        if (Workspace?.CommandBatch.HasResults != true)
        {
            FlashSteps(StepOf(sender) is { } step ? [step] : []);
            AppLog.Hint("Копировать нечего — команда ещё не выполнялась.", LogLevel.Warning);
            return;
        }

        if (Window.GetWindow(this) is MainWindow main)
        {
            main.Terminal.CopyCurrent();
        }
    }

    // ── кнопки без дела ─────────────────────────────────────────────────────

    /// <summary>Два коротких вспыха — тусклым оранжевым, как плашка предупреждения.</summary>
    internal static DoubleAnimation Blink() =>
        new(0, 1, TimeSpan.FromMilliseconds(180))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(2),
            FillBehavior = FillBehavior.Stop,
        };

    /// <summary>Нажали кнопку, которой нечего делать: рамки этих окон мигают.</summary>
    private void FlashSteps(IEnumerable<CommandStep> steps)
    {
        foreach (var step in steps)
        {
            if (StepsList.ItemContainerGenerator.ContainerFromItem(step) is ContentPresenter presenter
                && presenter.ContentTemplate?.FindName("Flash", presenter) is UIElement flash)
            {
                flash.BeginAnimation(OpacityProperty, Blink());
            }
        }
    }

    /// <summary>Значок «Вставить» тусклый, пока в буфере нет текста.</summary>
    private void ListenClipboard()
    {
        HasClipboardText = ClipboardWatch.HasText;
        ClipboardWatch.Attach(this);
    }
}
