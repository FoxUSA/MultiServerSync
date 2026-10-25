using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MultiServerSync.Models;
using MultiServerSync.Services;
using MultiServerSync.Views.Dialogs;

namespace MultiServerSync.Views.Pages;

/// <summary>
/// Страница команды. Текст команды и sudo принадлежат текущей вкладке,
/// история и избранные команды общие для всех вкладок.
/// </summary>
public partial class CommandPage : Page
{
    public CommandPage()
    {
        InitializeComponent();

        // история и избранное сквозные — одни на все вкладки
        HistoryBox.ItemsSource = AppState.Current.Settings.CommandHistory;
        BookBox.ItemsSource = AppState.Current.Settings.CommandBook;

        DataContext = AppState.Current.ActiveWorkspace;
        AppState.Current.ActiveWorkspaceChanged += workspace => DataContext = workspace;

        // выбор в списках держится только пока поле ввода совпадает с выбранной строкой
        DataContextChanged += OnWorkspaceSwitched;
        Attach(Workspace);

        // Ctrl+V и «Вставить» из меню поля проходят ту же проверку, что и значок вставки
        DataObject.AddPastingHandler(CommandBox, OnCommandBoxPasting);
        PasteToast.Large = true;

        KeepBoxBackground();
        Loaded += (_, _) => KeepBoxBackground();
        Wpf.Ui.Appearance.ApplicationThemeManager.Changed += (_, _) => KeepBoxBackground();

        // ярлыки вкладок пишут статистику по открытому разделу
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                AppState.Current.Section = AppSection.Command;
            }
        };
    }

    private Workspace? Workspace => DataContext as Workspace;

    /// <summary>
    /// Фон поля команды не меняется ни под мышью, ни в фокусе. Тема подменяет его на своих
    /// состояниях, и на поле в половину страницы это читается как мигание. Ключи состояний
    /// переопределяются у самого поля — соседних элементов это не касается — и берут ту же
    /// кисть, что и покой, поэтому переопределение переживает смену темы.
    /// </summary>
    private void KeepBoxBackground()
    {
        if (CommandBox.TryFindResource("TextControlBackground") is not Brush quiet)
        {
            return;
        }

        CommandBox.Resources["TextControlBackgroundPointerOver"] = quiet;
        CommandBox.Resources["TextControlBackgroundFocused"] = quiet;
    }

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
    }

    private void OnWorkspaceProperty(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Services.Workspace.CommandText))
        {
            SyncPickers();
        }
    }

    /// <summary>
    /// Снимает выбор в списках, как только поле ввода перестало совпадать с выбранной строкой.
    /// Список сообщает о выборе только когда тот сменился: пока в нём висит прошлая запись,
    /// повторный выбор той же избранной команды ничего не подставлял, и приходилось выбирать
    /// сначала другую запись, а потом нужную.
    /// </summary>
    private void SyncPickers()
    {
        var text = Workspace?.CommandText ?? string.Empty;

        if (BookBox.SelectedItem is CommandSnippet snippet
            && !string.Equals(snippet.Command, text, StringComparison.Ordinal))
        {
            BookBox.SelectedItem = null;
        }

        if (HistoryBox.SelectedItem is string command
            && !string.Equals(command, text, StringComparison.Ordinal))
        {
            HistoryBox.SelectedItem = null;
        }
    }

    private void OnHistoryPicked(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && HistoryBox.SelectedItem is string text && Workspace is { } workspace)
        {
            workspace.CommandText = text;
            AppLog.Info("Подставлена команда из истории: " + OneLine(text));
        }
    }

    private void OnClearCommand(object sender, RoutedEventArgs e)
    {
        if (Workspace is { } workspace)
        {
            if (string.IsNullOrWhiteSpace(workspace.CommandText))
            {
                AppLog.Info("Поле команды и так пустое.");
                return;
            }

            workspace.CommandText = string.Empty;
            AppLog.Info("Поле команды очищено.");
        }

        HistoryBox.SelectedItem = null;
    }

    /// <summary>
    /// Вставка из буфера заменяет содержимое поля целиком, а не дописывается в конец:
    /// иначе новый скрипт склеивается с прошлой командой без перевода строки.
    /// </summary>
    private void OnPasteCommand(object sender, RoutedEventArgs e)
    {
        if (Workspace is not { } workspace)
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
            AppLog.Warn("Не удалось прочитать буфер обмена: " + ex.Message);
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            AppLog.Warn("В буфере обмена нет текста.");
            return;
        }

        if (ClipboardText.TerminalOutputWarning(text) is { } warning)
        {
            PasteToast.ShowInPlace(AppLog.WarnInPlace(warning));
            return;
        }

        workspace.CommandText = text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        AppLog.Info($"Вставлено из буфера ({workspace.CommandText.Length} символов): {OneLine(workspace.CommandText)}");

        CommandBox.Focus();
        CommandBox.CaretIndex = CommandBox.Text.Length;
    }

    /// <summary>
    /// Вставка в поле команды с клавиатуры или из меню: вывод терминала, который сами же
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

    /// <summary>Выбор записи избранного подставляет её команду в поле ввода.</summary>
    private void OnSnippetPicked(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && BookBox.SelectedItem is CommandSnippet snippet && Workspace is { } workspace)
        {
            workspace.CommandText = snippet.Command;
            AppLog.Info($"Подставлена команда из избранного: {snippet.Comment}");
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

    /// <summary>
    /// Записывает команду из поля ввода в избранное. Название спрашивается в отдельном окне;
    /// если такое название уже есть, запись обновляется — так же правится и сама команда.
    /// </summary>
    private void OnAddSnippet(object sender, RoutedEventArgs e)
    {
        var command = (Workspace?.CommandText ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(command))
        {
            AppLog.Warn("Сначала наберите команду в поле ввода — её и запишем в избранное.");
            return;
        }

        var current = (BookBox.SelectedItem as CommandSnippet)?.Comment ?? string.Empty;

        var dialog = new InputDialog(
            "Запись в избранное",
            "Как назвать эту команду? Короткое название:",
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
            existing = new CommandSnippet { Comment = comment, Command = command };
            book.Add(existing);
            AppLog.Success($"В избранное добавлено: {comment}");
        }
        else
        {
            existing.Command = command;
            AppLog.Success($"Запись избранного обновлена: {comment}");
        }

        AppState.Current.SaveSettings();
        BookBox.SelectedItem = existing;
    }

    /// <summary>Карандаш в выпадающем списке: правит название и код записи избранного.</summary>
    private void OnEditSnippet(object sender, RoutedEventArgs e)
    {
        // иначе щелчок дойдёт до строки списка и она будет выбрана
        e.Handled = true;

        if (sender is not FrameworkElement { DataContext: CommandSnippet snippet })
        {
            return;
        }

        // модальное окно поверх открытого списка выглядит криво — список закрываем
        BookBox.IsDropDownOpen = false;

        var dialog = new SnippetEditDialog("Изменить запись избранного", snippet.Comment, snippet.Command)
        {
            Owner = Window.GetWindow(this),
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var book = AppState.Current.Settings.CommandBook;

        // название — ключ записи: два одинаковых в списке не различить
        var clash = book.FirstOrDefault(s =>
            !ReferenceEquals(s, snippet)
            && string.Equals(s.Comment, dialog.SnippetName, StringComparison.OrdinalIgnoreCase));

        if (clash is not null)
        {
            AppLog.Warn($"В избранном уже есть запись «{clash.Comment}» — выберите другое название.");
            return;
        }

        snippet.Comment = dialog.SnippetName;
        snippet.Command = dialog.Code;

        AppState.Current.SaveSettings();
        AppLog.Success($"Запись избранного изменена: {snippet.Comment}");
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
        AppLog.Info($"Из избранного удалено: {snippet.Comment}");

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
        AppLog.Info($"Из истории команд удалено: {value}");

        HistoryBox.IsDropDownOpen = history.Count > 0;
    }

    /// <summary>
    /// Команда выполняется на отмеченных серверах открытой командной вкладки.
    /// У каждой вкладки свои команда, серверы и терминал.
    /// </summary>
    private async void OnRun(object sender, RoutedEventArgs e)
    {
        if (Workspace is { } workspace)
        {
            await RunGuardedAsync(workspace);
        }
    }

    /// <summary>Сколько после окончания команды её повтор считается случайным двойным нажатием.</summary>
    private static readonly TimeSpan RepeatGuard = TimeSpan.FromSeconds(3);

    private Workspace? _lastTab;
    private string? _lastCommand;
    private DateTime _lastFinishedAt;
    private bool _repeatConfirmed;

    /// <summary>
    /// Запуск с защитой от двойного нажатия: та же команда сразу после окончания прошлой
    /// не выполняется — посреди поля просьба нажать ещё раз. Повторное нажатие запускает.
    /// </summary>
    private async Task RunGuardedAsync(Workspace workspace)
    {
        var command = (workspace.CommandText ?? string.Empty).Trim();

        if (AppState.Current.Settings.RepeatGuard
            && !_repeatConfirmed
            && command.Length > 0
            && ReferenceEquals(workspace, _lastTab)
            && command == _lastCommand
            && DateTime.Now - _lastFinishedAt < RepeatGuard)
        {
            _repeatConfirmed = true;
            PasteToast.ShowInPlace(AppLog.WarnInPlace(
                "Команда только что выполнена — для повтора нажмите ещё раз.\n"
                + "Отключить: Настройки → Защита от двойного запуска."));
            return;
        }

        _repeatConfirmed = false;

        if (await workspace.RunCommandAsync())
        {
            _lastTab = workspace;
            _lastCommand = command;
            _lastFinishedAt = DateTime.Now;
        }
    }

    /// <summary>■ над полем: Ctrl+C командам на серверах — то же прерывание, что в терминале.</summary>
    private async void OnInterrupt(object sender, RoutedEventArgs e) => await InterruptAsync();

    private async Task InterruptAsync()
    {
        if (Workspace is { CommandBatch.IsRunning: true } workspace)
        {
            await workspace.CommandBatch.InterruptAsync();
        }
    }

    /// <summary>
    /// Пульт с клавиатуры, не отрывая рук от поля: Ctrl+Enter запускает, а пока команда идёт —
    /// прерывает; Esc тоже прерывает; Ctrl+Shift+C забирает вывод. Обычный Enter — перевод строки.
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
                await RunGuardedAsync(workspace);
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

    /// <summary>Значок над полем: то же, что «Копировать. Текущее окно» в терминале внизу.</summary>
    private void OnCopyOutput(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.Terminal.CopyCurrent();
        }
    }
}
