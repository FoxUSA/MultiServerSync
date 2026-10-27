using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    public static readonly DependencyProperty IsNotEditingSnippetProperty = DependencyProperty.Register(
        nameof(IsNotEditingSnippet), typeof(bool), typeof(CommandPage), new PropertyMetadata(true));

    /// <summary>Тик секундомеров окон — один на страницу, а не по таймеру на окно.</summary>
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(250) };

    /// <summary>Окно, в котором стоял курсор: туда подставляется история.</summary>
    private CommandStep? _activeStep;

    // ── правка избранного ──
    private CommandSnippet? _editing;
    private Workspace? _editWorkspace;

    public CommandPage()
    {
        InitializeComponent();

        // история и избранное сквозные — одни на все вкладки
        HistoryBox.ItemsSource = AppState.Current.Settings.CommandHistory;
        BookBox.ItemsSource = AppState.Current.Settings.CommandBook;

        DataContext = AppState.Current.ActiveWorkspace;
        AppState.Current.ActiveWorkspaceChanged += workspace => DataContext = workspace;

        // выбор в списках держится только пока окно совпадает с выбранной строкой
        DataContextChanged += OnWorkspaceSwitched;
        Attach(Workspace);

        PasteToast.Large = true;

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

    /// <summary>Не идёт правка избранного — тогда работает всё, а не только окна и «Сохранить».</summary>
    public bool IsNotEditingSnippet
    {
        get => (bool)GetValue(IsNotEditingSnippetProperty);
        private set => SetValue(IsNotEditingSnippetProperty, value);
    }

    private Workspace? Workspace => DataContext as Workspace;

    /// <summary>Окно, куда подставлять: последнее с курсором, если оно ещё есть, иначе первое.</summary>
    private CommandStep? ActiveStep =>
        _activeStep is { } step && Workspace?.Steps.Contains(step) == true ? step : Workspace?.Steps.FirstOrDefault();

    private static CommandStep? StepOf(object sender) => (sender as FrameworkElement)?.DataContext as CommandStep;

    // ── окна ────────────────────────────────────────────────────────────────

    private void OnStepBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Control box)
        {
            PaintBox(box);
        }
    }

    private void OnStepFocused(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (StepOf(sender) is { } step)
        {
            _activeStep = step;
            SyncPickers();
        }
    }

    /// <summary>
    /// Фон окна не меняется ни под мышью, ни в фокусе: тема подменяет его на своих состояниях,
    /// и на окне в полстраницы это читается как мигание. В правке избранного фон подкрашен —
    /// так видно, что сейчас правится запись, а не рабочие команды.
    /// </summary>
    private void PaintBox(Control box)
    {
        var brush = !IsNotEditingSnippet
            ? (Brush)FindResource("EditTint")
            : TryFindResource("TextControlBackground") as Brush;

        if (brush is null)
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
        AppLog.Hint($"Добавлено окно #{step.Number}. Окон: {workspace.Steps.Count}.");
        FocusStep(step);
    }

    private void OnRemoveStep(object sender, RoutedEventArgs e)
    {
        if (Workspace is not { } workspace || StepOf(sender) is not { } step)
        {
            return;
        }

        var number = step.Number;

        if (workspace.RemoveStep(step))
        {
            AppLog.Hint($"Окно #{number} убрано. Окон: {workspace.Steps.Count}.");
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
            AppLog.Hint("Окно и так пустое.");
            return;
        }

        step.Text = string.Empty;
        AppLog.Hint(Workspace?.Steps.Count > 1 ? $"Окно #{step.Number} очищено." : "Поле команды очищено.");
        HistoryBox.SelectedItem = null;
    }

    // ── переключение вкладки ────────────────────────────────────────────────

    private void OnWorkspaceSwitched(object sender, DependencyPropertyChangedEventArgs e)
    {
        // правка избранного живёт в окнах той вкладки, где её начали: уходим — правка отменяется
        if (_editing is not null)
        {
            CancelEdit();
        }

        if (e.OldValue is Workspace old)
        {
            old.PropertyChanged -= OnWorkspaceProperty;
        }

        _activeStep = null;
        Attach(e.NewValue as Workspace);
    }

    private void Attach(Workspace? workspace)
    {
        if (workspace is not null)
        {
            workspace.PropertyChanged += OnWorkspaceProperty;
        }

        SyncPickers();
    }

    private void OnWorkspaceProperty(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Services.Workspace.CommandText))
        {
            SyncPickers();
        }
    }

    /// <summary>
    /// Снимает выбор в списках, как только окно перестало совпадать с выбранной строкой.
    /// Список сообщает о выборе только когда тот сменился: пока в нём висит прошлая запись,
    /// повторный выбор той же избранной команды ничего не подставлял.
    /// </summary>
    private void SyncPickers()
    {
        if (Workspace is not { } workspace)
        {
            return;
        }

        var text = ActiveStep?.Text ?? string.Empty;

        if (BookBox.SelectedItem is CommandSnippet snippet)
        {
            var same = snippet.IsMulti
                ? Signature(snippet.AsSteps()) == Signature(workspace.SaveSteps())
                : string.Equals(snippet.Command, text, StringComparison.Ordinal);

            if (!same)
            {
                BookBox.SelectedItem = null;
            }
        }

        if (HistoryBox.SelectedItem is string command
            && !string.Equals(command, text, StringComparison.Ordinal))
        {
            HistoryBox.SelectedItem = null;
        }
    }

    private static string Signature(IEnumerable<SavedCommandStep> steps) =>
        string.Join("\n␞\n", steps.Select(s => s.Text.Trim()));

    // ── история и вставка ───────────────────────────────────────────────────

    private void OnHistoryPicked(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && HistoryBox.SelectedItem is string text && ActiveStep is { } step)
        {
            step.Text = text;
            AppLog.Hint("Подставлена команда из истории: " + OneLine(text));
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
            AppLog.Hint("Не удалось прочитать буфер обмена: " + ex.Message, LogLevel.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            AppLog.Hint("В буфере обмена нет текста.", LogLevel.Warning);
            return;
        }

        if (ClipboardText.TerminalOutputWarning(text) is { } warning)
        {
            PasteToast.ShowInPlace(AppLog.WarnInPlace(warning));
            return;
        }

        step.Text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        AppLog.Hint($"Вставлено из буфера ({step.Text.Length} символов): {OneLine(step.Text)}");

        _activeStep = step;
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
    private static string OneLine(string text)
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
    /// Выбор записи избранного: окна вкладки заменяются её набором — у одиночной остаётся
    /// одно окно с этой командой (sudo — как было у окна с курсором).
    /// </summary>
    private void OnSnippetPicked(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0 || BookBox.SelectedItem is not CommandSnippet snippet || Workspace is not { } workspace)
        {
            return;
        }

        if (snippet.IsMulti)
        {
            workspace.SetSteps(snippet.AsSteps());
            _activeStep = workspace.Steps[0];
            AppLog.Hint($"Из избранного раскрыто окон: {workspace.Steps.Count} — {snippet.Comment}");
        }
        else
        {
            var sudo = ActiveStep?.UseSudo ?? false;
            workspace.SetSteps([new SavedCommandStep { Text = snippet.Command, UseSudo = sudo }]);
            _activeStep = workspace.Steps[0];
            AppLog.Hint($"Подставлена команда из избранного: {snippet.Comment}");
        }
    }

    /// <summary>♡ у окна: записать это окно в избранное одиночной записью.</summary>
    private void OnAddSnippet(object sender, RoutedEventArgs e)
    {
        if (StepOf(sender) is not { } step || string.IsNullOrWhiteSpace(step.Text))
        {
            AppLog.Hint("Сначала наберите команду в окне — её и запишем в избранное.", LogLevel.Warning);
            return;
        }

        SaveSnippet([new SavedCommandStep { Text = step.Text.Trim(), UseSudo = step.UseSudo }]);
    }

    /// <summary>♡♡ у первого окна: все окна одной многозадачной записью.</summary>
    private void OnAddSnippetSet(object sender, RoutedEventArgs e)
    {
        var steps = Workspace?.Steps
            .Where(s => !string.IsNullOrWhiteSpace(s.Text))
            .Select(s => new SavedCommandStep { Text = s.Text.Trim(), UseSudo = s.UseSudo })
            .ToList() ?? [];

        if (steps.Count == 0)
        {
            AppLog.Hint("Все окна пустые — записывать в избранное нечего.", LogLevel.Warning);
            return;
        }

        SaveSnippet(steps);
    }

    /// <summary>
    /// Название спрашивается в отдельном окне; если такое название уже есть, запись
    /// обновляется — так же правится и сама команда.
    /// </summary>
    private void SaveSnippet(List<SavedCommandStep> steps)
    {
        var current = (BookBox.SelectedItem as CommandSnippet)?.Comment ?? string.Empty;
        var multi = steps.Count > 1;

        var dialog = new InputDialog(
            "Запись в избранное",
            multi ? $"Как назвать этот набор из {steps.Count} окон? Короткое название:" : "Как назвать эту команду? Короткое название:",
            current)
        {
            Owner = Window.GetWindow(this),
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Value))
        {
            return;
        }

        var comment = dialog.Value.Trim();
        var book = AppState.Current.Settings.CommandBook;
        var existing = book.FirstOrDefault(s => string.Equals(s.Comment, comment, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            existing = new CommandSnippet { Comment = comment };
            existing.SetSteps(steps);
            book.Add(existing);
            AppLog.Hint(multi ? $"В избранное добавлен набор из {steps.Count} окон: {comment}" : $"В избранное добавлено: {comment}");
        }
        else
        {
            existing.SetSteps(steps);
            AppLog.Hint($"Запись избранного обновлена: {comment}");
        }

        AppState.Current.SaveSettings();
        BookBox.SelectedItem = existing;
    }

    /// <summary>
    /// Карандаш: запись открывается на правку прямо в окнах — так, как она будет выглядеть
    /// в работе. Окна подкрашены, работают только правка, ＋/－, «Сохранить» и «Отменить».
    /// Рабочие команды вкладки на время правки откладываются и потом возвращаются.
    /// </summary>
    private void OnEditSnippet(object sender, RoutedEventArgs e)
    {
        // иначе щелчок дойдёт до строки списка и она будет выбрана
        e.Handled = true;

        if (sender is not FrameworkElement { DataContext: CommandSnippet snippet } || Workspace is not { } workspace)
        {
            return;
        }

        // модальное окно и правка поверх открытого списка выглядят криво — список закрываем
        BookBox.IsDropDownOpen = false;

        if (workspace.CommandBatch.IsRunning)
        {
            AppLog.Hint("Команда ещё выполняется — править избранное можно, когда она закончится.", LogLevel.Warning);
            return;
        }

        _editing = snippet;
        _editWorkspace = workspace;
        workspace.HoldSteps(snippet.AsSteps());
        EditNameBox.Text = snippet.Comment;
        EditBar.Visibility = Visibility.Visible;
        IsNotEditingSnippet = false;

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(PaintBoxes));
        AppLog.Hint($"Правка избранного: {snippet.Comment}");
    }

    private void OnSaveEdit(object sender, RoutedEventArgs e)
    {
        if (_editing is not { } snippet || _editWorkspace is not { } workspace)
        {
            return;
        }

        var name = EditNameBox.Text.Trim();

        if (name.Length == 0)
        {
            AppLog.Hint("У записи должно быть название.", LogLevel.Warning);
            EditNameBox.Focus();
            return;
        }

        var book = AppState.Current.Settings.CommandBook;

        // название — ключ записи: два одинаковых в списке не различить
        if (book.FirstOrDefault(s => !ReferenceEquals(s, snippet)
                && string.Equals(s.Comment, name, StringComparison.OrdinalIgnoreCase)) is { } clash)
        {
            AppLog.Hint($"В избранном уже есть запись «{clash.Comment}» — выберите другое название.", LogLevel.Warning);
            EditNameBox.Focus();
            return;
        }

        var steps = workspace.Steps
            .Where(s => !string.IsNullOrWhiteSpace(s.Text))
            .Select(s => new SavedCommandStep { Text = s.Text.Trim(), UseSudo = s.UseSudo })
            .ToList();

        if (steps.Count == 0)
        {
            AppLog.Hint("Все окна пустые — запись без команд не сохраняется. Удалить её можно крестиком в списке.", LogLevel.Warning);
            return;
        }

        snippet.Comment = name;
        snippet.SetSteps(steps);
        AppState.Current.SaveSettings();
        AppLog.Hint($"Запись избранного изменена: {name}");

        EndEdit();
    }

    private void OnCancelEdit(object sender, RoutedEventArgs e)
    {
        AppLog.Hint("Правка избранного отменена.");
        CancelEdit();
    }

    private void CancelEdit() => EndEdit();

    /// <summary>Вернуть вкладке её рабочие окна и выйти из правки.</summary>
    private void EndEdit()
    {
        _editWorkspace?.ReleaseSteps();

        _editing = null;
        _editWorkspace = null;

        EditBar.Visibility = Visibility.Collapsed;
        IsNotEditingSnippet = true;
        BookBox.SelectedItem = null;

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(PaintBoxes));
    }

    /// <summary>Крестик в выпадающем списке: убирает запись из избранного.</summary>
    private void OnRemoveSnippet(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is not FrameworkElement { DataContext: CommandSnippet snippet })
        {
            return;
        }

        var book = AppState.Current.Settings.CommandBook;

        if (!book.Remove(snippet))
        {
            return;
        }

        AppState.Current.SaveSettings();
        AppLog.Hint($"Из избранного удалено: {snippet.Comment}");

        BookBox.IsDropDownOpen = book.Count > 0;
    }

    /// <summary>Крестик в выпадающем списке: убирает одну команду из истории.</summary>
    private void OnRemoveHistoryItem(object sender, RoutedEventArgs e)
    {
        // иначе щелчок дойдёт до строки списка и она будет выбрана
        e.Handled = true;

        if (sender is not FrameworkElement { DataContext: string value })
        {
            return;
        }

        var history = AppState.Current.Settings.CommandHistory;

        if (!history.Remove(value))
        {
            return;
        }

        AppState.Current.SaveSettings();
        AppLog.Hint($"Из истории команд удалено: {value}");

        HistoryBox.IsDropDownOpen = history.Count > 0;
    }

    // ── запуск ──────────────────────────────────────────────────────────────

    /// <summary>«Выполнить тут»: все окна вкладки по очереди на её отмеченных серверах.</summary>
    private async void OnRun(object sender, RoutedEventArgs e)
    {
        if (Workspace is { } workspace)
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
    /// «Выполнить везде» — команды запускаются во всех командных вкладках сразу, каждая со своими
    /// окнами и серверами. Личные вкладки копирования не запускаются: у них свой момент —
    /// после удачного копирования. Сводка показывается на вкладке, с которой нажали.
    /// </summary>
    private async void OnRunEverywhere(object sender, RoutedEventArgs e)
    {
        if (Workspace is not { } workspace)
        {
            return;
        }

        var all = AppState.Current.CommandTabs.Where(t => !t.IsPersonal).ToList();

        // широкое действие: команды уходят со всех вкладок сразу — сначала спросить.
        // По умолчанию фокус на «Отмене»
        if (!ConfirmDialog.Ask(
                Window.GetWindow(this),
                $"Запустить команды на всех вкладках ({all.Count})?",
                string.Empty,
                "Выполнить везде"))
        {
            AppLog.Hint("«Выполнить везде» отменено.");
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
            AppLog.Info("«Выполнить везде»: запускать нечего — все вкладки пропущены.");
            return;
        }

        if (chosen.Count == 1)
        {
            await chosen[0].RunCommandAsync(approved: true);
            return;
        }

        AppLog.Info($"«Выполнить везде» — команды запускаются во всех вкладках: {chosen.Count}.");

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
        if (!IsNotEditingSnippet)
        {
            return;
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
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.Terminal.CopyCurrent();
        }
    }
}
