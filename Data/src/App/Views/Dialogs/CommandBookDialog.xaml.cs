using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MultiServerSync.Core;
using MultiServerSync.Models;
using MultiServerSync.Services;
using MultiServerSync.Views.Controls;
using Wpf.Ui.Controls;

namespace MultiServerSync.Views.Dialogs;

/// <summary>Откуда открыли избранное и что можно в него добавить.</summary>
public sealed class CommandBookRequest
{
    public required AppSection Section { get; init; }

    public Workspace? Tab { get; init; }

    /// <summary>Окно, у которого нажали ♡: его команду добавляет «Добавить в избранное».</summary>
    public SavedCommandStep Window { get; init; } = new();

    /// <summary>Непустые окна вкладки — только у «Команды»; больше одного — есть «Добавить все окна».</summary>
    public IReadOnlyList<SavedCommandStep> AllWindows { get; init; } = [];

    /// <summary>Запись из нескольких окон можно вставить: только «Команда», у мини-терминала окно одно.</summary>
    public bool AllowMulti { get; init; }

    /// <summary>Команда из истории: окно открывается сразу новой записью с ней.</summary>
    public string? AddCommand { get; init; }
}

/// <summary>
/// Окно избранного, одно на все разделы. Слева карточка вкладок, справа карточка выбранной вкладки:
/// щелчок по команде отдаёт её и закрывает окно. Здесь же добавление команды из окна, правка записи
/// (название, вкладка, текст), удаление, перенос записи броском на вкладку и сами вкладки —
/// новая, переименовать, удалить, порядок мышью.
/// </summary>
public partial class CommandBookDialog : FluentWindow
{
    /// <summary>Ниже этого окно команды в правке не ужимается — дальше прокрутка.</summary>
    private const double MinDraftHeight = 130;

    private readonly CommandBookRequest _request;
    private string _group;

    /// <summary>Справа история команд вкладки программы, а не вкладка избранного.</summary>
    private bool _history;

    private CommandSnippet? _picked;
    private List<GroupRow> _groupRows = [];

    // ── правка: _target null — запись новая ──
    private bool _editing;
    private CommandSnippet? _target;

    /// <summary>Какая вкладка была открыта до правки: «Отмена» возвращает к ней.</summary>
    private string _groupBeforeEdit = string.Empty;

    private int _draftCount;

    /// <summary>Запись тащат над карточкой вкладок.</summary>
    private bool _overGroups;

    /// <summary>Что схватили мышью и в каком списке — для плашки за курсором.</summary>
    private (ItemsControl List, string Name, string Preview)? _grabbed;

    /// <summary>Была ли открыта история до правки: «Отмена» возвращает к ней.</summary>
    private bool _historyBeforeEdit;

    /// <summary>Окно открыли ♡ у строки истории сразу новой записью: «Сохранить» и «Отмена» закрывают его, а не ведут в список.</summary>
    private bool _quickAdd;

    private CommandBookDialog(CommandBookRequest request)
    {
        InitializeComponent();

        _request = request;
        CommandBook.Normalize();

        // у каждой вкладки программы своё место в избранном; новая начинает с первой вкладки избранного
        _group = request.Tab?.BookGroup is { } last && CommandBook.Groups.Contains(last) ? last : CommandBook.Groups[0];
        _history = request.Tab?.BookHistory == true;

        // история — у вкладки программы; без вкладки её нет
        if (request.Tab is null)
        {
            HistoryPlate.Visibility = Visibility.Collapsed;
        }

        AddOne.IsEnabled = !string.IsNullOrWhiteSpace(request.Window.Text);
        AddOne.ToolTip = AddOne.IsEnabled
            ? "Команду из окна — в эту вкладку избранного. Название спросит здесь же."
            : "Окно пустое — добавлять нечего.";

        if (request.AllWindows.Count > 1)
        {
            AddAll.Content = $"Все окна ({request.AllWindows.Count})";
            AddAll.ToolTip = "Все окна одной записью: при вставке они раскроются сами.";
            AddAll.Visibility = Visibility.Visible;
        }

        _ = new TabReorder(GroupsList, GroupsScroll, _ => !_editing, fixedCount: 0, (item, to) =>
        {
            if (item is GroupRow row)
            {
                CommandBook.MoveGroup(row.Name, to);
                Rebuild();
            }
        });

        // запись переставляется внутри вкладки, а брошенная на вкладку слева — уезжает в неё
        _ = new TabReorder(BookList, BookScroll, _ => !_editing, fixedCount: 0, (item, to) =>
        {
            if (item is SnippetRow row)
            {
                CommandBook.MoveSnippet(row.Snippet, to);
                Rebuild();
            }
        })
        {
            LiftShadow = false,
            OutTarget = GroupsPane,
            DroppedOut = DropOnGroup,
            OverOutTarget = over =>
            {
                _overGroups = over;
                MarkDropTarget();
            },
        };

        // команду из истории не переставляют, а бросают на вкладку слева — и она становится записью избранного
        _ = new TabReorder(HistoryList, null, _ => !_editing, fixedCount: 0, (_, _) => { })
        {
            Reorder = false,
            LiftShadow = false,
            OutTarget = GroupsPane,
            DroppedOut = DropHistoryOnGroup,
            OverOutTarget = over =>
            {
                _overGroups = over;
                MarkDropTarget();
            },
        };

        BookList.PreviewMouseLeftButtonDown += (_, e) =>
            _grabbed = RowOf<SnippetRow>(e.OriginalSource as DependencyObject) is { } row ? (BookList, row.Comment, row.Preview) : null;
        HistoryList.PreviewMouseLeftButtonDown += (_, e) =>
            _grabbed = RowOf<HistoryRow>(e.OriginalSource as DependencyObject) is { } row ? (HistoryList, string.Empty, row.Preview) : null;
        BookList.LostMouseCapture += (_, _) => Ghost.Visibility = Visibility.Collapsed;
        HistoryList.LostMouseCapture += (_, _) => Ghost.Visibility = Visibility.Collapsed;

        DraftScroll.SizeChanged += (_, _) => FitDraft();

        // «Сохранить» в правке — только когда что-то поменяли
        SaveGate.Watch(this, SaveButton, EditChanged);

        Rebuild();

        if (request.Tab is { } tab && (_history || tab.BookGroup == _group))
        {
            Loaded += (_, _) => (_history ? HistoryScroll : BookScroll).ScrollToVerticalOffset(tab.BookScroll);
        }

        Closed += (_, _) => request.Tab?.SetBookPlace(_group, (_history ? HistoryScroll : BookScroll).VerticalOffset, _history);

        if (request.AddCommand is { } adding && !string.IsNullOrWhiteSpace(adding))
        {
            Loaded += (_, _) =>
            {
                StartEdit(null, string.Empty, [new SavedCommandStep { Text = adding.Trim() }]);
                _quickAdd = true;
            };
        }
    }

    /// <summary>Для сообщения о вставке: откуда взята команда — из избранного или из истории.</summary>
    public static string Origin(CommandSnippet picked) =>
        picked.Group.Length == 0 ? $"из истории: {picked.Comment}" : $"из избранного: {picked.Comment}";

    /// <summary>Открыть избранное. Возвращает выбранную запись или null, если закрыли без выбора.</summary>
    public static CommandSnippet? Ask(Window? owner, CommandBookRequest request)
    {
        var dialog = new CommandBookDialog(request);

        if (owner is { IsLoaded: true })
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return dialog.ShowDialog() == true ? dialog._picked : null;
    }

    private void Rebuild()
    {
        if (!CommandBook.Groups.Contains(_group))
        {
            _group = CommandBook.Groups[0];
        }

        _groupRows = CommandBook.Groups
            .Select(g => new GroupRow(g, CommandBook.In(g).Count, !_history && g == _group))
            .ToList();
        GroupsList.ItemsSource = _groupRows;
        HistoryRing.Visibility = _history ? Visibility.Visible : Visibility.Collapsed;

        BookScroll.Visibility = _history ? Visibility.Collapsed : Visibility.Visible;
        HistoryScroll.Visibility = _history ? Visibility.Visible : Visibility.Collapsed;

        int count;

        if (_history)
        {
            var history = _request.Tab?.CommandHistory ?? [];
            HistoryList.ItemsSource = history.Select(t => new HistoryRow(t)).ToList();
            BookList.ItemsSource = null;
            count = history.Count;
            EmptyText.Text = "Команд пока не было. Сюда попадает каждая команда, выполненная в этой вкладке.";
            ClearHistory.IsEnabled = count > 0;
        }
        else
        {
            var rows = CommandBook.In(_group).Select(s => new SnippetRow(s, _request.AllowMulti)).ToList();
            BookList.ItemsSource = rows;
            HistoryList.ItemsSource = null;
            count = rows.Count;
            EmptyText.Text = "Во вкладке пока пусто. Команду из окна добавляет «Добавить в избранное» вверху, а запись из другой вкладки можно бросить мышью на эту вкладку слева.";
        }

        EmptyText.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // в правке подвал занят двумя кнопками, а куда сохранить, и так сказано слева и в шапке
        FooterHint.Text = _editing ? string.Empty
            : _history ? "Перетащите команду на вкладку слева, чтобы добавить в избранное"
            : "Записи можно перетаскивать: менять местами или на другую вкладку";

        if (_editing)
        {
            // запись сохранится во вкладку, выбранную слева
            HeaderCount.Text = $"во вкладку «{_group}»" + (_draftCount > 1 ? $" · окон: {_draftCount}" : string.Empty);
            SaveGate.Show(SaveButton, EditChanged());
        }
        else
        {
            HeaderText.Text = _history ? "История" : _group;
            HeaderCount.Text = _history ? Plural(count, "команда", "команды", "команд") : Plural(count, "запись", "записи", "записей");
            AddButtons.Visibility = _history ? Visibility.Collapsed : Visibility.Visible;
            ClearHistory.Visibility = _history ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static string Plural(int n, string one, string few, string many)
    {
        var tail = (n % 100) is >= 11 and <= 14 ? many
            : (n % 10) switch { 1 => one, 2 or 3 or 4 => few, _ => many };
        return $"{n} {tail}";
    }

    private void Warn(string text) => Toast.ShowInPlace(AppLog.WarnInPlace(text));

    // ── вкладки ─────────────────────────────────────────────────────────────

    private void OnGroupClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GroupRow row } && (_history || row.Name != _group))
        {
            _group = row.Name;
            _history = false;
            Rebuild();
            BookScroll.ScrollToTop();
        }
    }

    private void OnGroupMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement { DataContext: GroupRow row })
        {
            e.Handled = true;
            RenameGroup(row.Name);
        }
    }

    private void OnRenameGroup(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GroupRow row })
        {
            RenameGroup(row.Name);
        }
    }

    private void RenameGroup(string name)
    {
        if (AskName("Вкладка избранного", "Новое название вкладки:", name) is not { } value)
        {
            return;
        }

        if (CommandBook.RenameGroup(name, value, _request.Section, _request.Tab) is { } refusal)
        {
            Warn(refusal);
            return;
        }

        if (_group == name)
        {
            _group = value;
        }

        Rebuild();
    }

    private void OnRemoveGroup(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GroupRow row })
        {
            return;
        }

        if (CommandBook.Groups.Count < 2)
        {
            Warn("Это единственная вкладка избранного — её можно переименовать, но не удалить.");
            return;
        }

        // записи пропадут во всех разделах — спросить, если они есть
        if (row.Count > 0 && !ConfirmDialog.Ask(
                this,
                $"Удалить вкладку «{row.Name}» вместе с командами ({row.Count})?",
                string.Join("\n", CommandBook.In(row.Name).Select(s => s.Comment)),
                "Удалить"))
        {
            return;
        }

        if (CommandBook.RemoveGroup(row.Name, _request.Section, _request.Tab) is { } refusal)
        {
            Warn(refusal);
            return;
        }

        Rebuild();
    }

    private void OnAddGroup(object sender, RoutedEventArgs e)
    {
        if (AskName("Новая вкладка избранного", "Название вкладки:", string.Empty) is not { } value)
        {
            return;
        }

        if (CommandBook.AddGroup(value, _request.Section, _request.Tab) is { } refusal)
        {
            Warn(refusal);
            return;
        }

        _group = value;
        _history = false;
        Rebuild();
        BookScroll.ScrollToTop();
    }

    private string? AskName(string title, string prompt, string current)
    {
        var dialog = new InputDialog(title, prompt, current) { Owner = this };
        return dialog.ShowDialog() == true && dialog.Value.Length > 0 ? dialog.Value : null;
    }

    // ── бросок записи на вкладку ────────────────────────────────────────────

    /// <summary>Вкладка под мышью, пока тащат запись; своя вкладка целью не считается, у команды из истории своей нет.</summary>
    private GroupRow? GroupUnderMouse()
    {
        var node = GroupsList.InputHitTest(Mouse.GetPosition(GroupsList)) as DependencyObject;

        while (node is not null)
        {
            if (node is FrameworkElement { DataContext: GroupRow row })
            {
                return !_history && row.Name == _group ? null : row;
            }

            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }

        return null;
    }

    private void MarkDropTarget()
    {
        var target = _overGroups ? GroupUnderMouse() : null;

        foreach (var row in _groupRows)
        {
            row.IsDropTarget = ReferenceEquals(row, target);
        }
    }

    private void OnWindowMouseMove(object sender, MouseEventArgs e)
    {
        if (_overGroups)
        {
            MarkDropTarget();
        }

        MoveGhost();
    }

    /// <summary>
    /// Запись в руке — одна плашка у курсора поверх всего окна: и в списке, и над вкладками.
    /// Сама строка в списке на это время — пустое место, куда запись встанет.
    /// </summary>
    private void MoveGhost()
    {
        if (_grabbed is not { } grabbed || !grabbed.List.IsMouseCaptured)
        {
            Ghost.Visibility = Visibility.Collapsed;
            return;
        }

        GhostName.Text = grabbed.Name;
        GhostName.Visibility = grabbed.Name.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        GhostPreview.Text = grabbed.Preview;
        GhostPreview.Margin = grabbed.Name.Length > 0 ? new Thickness(0, 2, 0, 0) : new Thickness(0);
        Ghost.Visibility = Visibility.Visible;

        var at = Mouse.GetPosition(DragLayer);
        Canvas.SetLeft(Ghost, at.X + 14);
        Canvas.SetTop(Ghost, at.Y + 10);
    }

    private static T? RowOf<T>(DependencyObject? node)
        where T : class
    {
        while (node is not null)
        {
            if (node is FrameworkElement { DataContext: T row })
            {
                return row;
            }

            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }

        return null;
    }

    /// <summary>Запись отпустили над вкладками: уезжает в ту, что под мышью, открытая вкладка не меняется.</summary>
    private void DropOnGroup(object item)
    {
        var target = GroupUnderMouse();
        _overGroups = false;
        MarkDropTarget();
        Ghost.Visibility = Visibility.Collapsed;

        if (item is not SnippetRow row || target is null)
        {
            return;
        }

        if (CommandBook.MoveToGroup(row.Snippet, target.Name, _request.Section, _request.Tab) is { } refusal)
        {
            Warn(refusal);
            return;
        }

        Rebuild();
    }

    /// <summary>Команду из истории отпустили на вкладке: открывается добавление в неё — осталось дать название.</summary>
    private void DropHistoryOnGroup(object item)
    {
        var target = GroupUnderMouse();
        _overGroups = false;
        MarkDropTarget();
        Ghost.Visibility = Visibility.Collapsed;

        if (item is HistoryRow row && target is not null)
        {
            StartEdit(null, string.Empty, [new SavedCommandStep { Text = row.Text }], into: target.Name);
        }
    }

    // ── команды ─────────────────────────────────────────────────────────────

    /// <summary>Щелчок по команде: она уходит в окно, избранное закрывается.</summary>
    private void OnRowClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SnippetRow row })
        {
            return;
        }

        if (!row.IsPickable)
        {
            Warn("Запись из нескольких окон вставляется только в разделе «Команда» — там она раскроет все окна.");
            return;
        }

        e.Handled = true;
        _picked = row.Snippet;
        DialogResult = true;
        Close();
    }

    private void OnEditSnippet(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SnippetRow row })
        {
            StartEdit(row.Snippet, row.Snippet.Comment, row.Snippet.AsSteps());
        }
    }

    private void OnCopySnippet(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SnippetRow row } && ClipboardText.Copy(row.Snippet.Command))
        {
            AppLog.Echo("Скопирована команда из избранного: " + row.Comment);
        }
    }

    private void OnRemoveSnippet(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SnippetRow row })
        {
            CommandBook.Remove(row.Snippet, _request.Section, _request.Tab);
            Rebuild();
        }
    }

    private void OnAddOne(object sender, RoutedEventArgs e) =>
        StartEdit(null, string.Empty, [new SavedCommandStep { Text = _request.Window.Text.Trim(), UseSudo = _request.Window.UseSudo }]);

    private void OnAddAll(object sender, RoutedEventArgs e) =>
        StartEdit(null, string.Empty, _request.AllWindows.Select(s => new SavedCommandStep { Text = s.Text.Trim(), UseSudo = s.UseSudo }).ToList());

    // ── история ─────────────────────────────────────────────────────────────

    private void OnShowHistory(object sender, RoutedEventArgs e)
    {
        if (_history)
        {
            return;
        }

        _history = true;
        Rebuild();
        HistoryScroll.ScrollToTop();
    }

    /// <summary>Щелчок по команде из истории: она уходит в окно, избранное закрывается.</summary>
    private void OnHistoryClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: HistoryRow row })
        {
            return;
        }

        // запись без вкладки избранного: так вызывающий видит, что она из истории
        var picked = new CommandSnippet { Comment = row.Preview };
        picked.SetSteps([new SavedCommandStep { Text = row.Text }]);

        e.Handled = true;
        _picked = picked;
        DialogResult = true;
        Close();
    }

    /// <summary>Крестик у команды: убирает её одну, без вопроса — как крестик у записи избранного.</summary>
    private void OnRemoveHistoryItem(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HistoryRow row } && _request.Tab is { } tab)
        {
            tab.ForgetCommand(row.Text, _request.Section);
            Rebuild();
        }
    }

    private void OnClearHistory(object sender, RoutedEventArgs e)
    {
        if (_request.Tab is not { CommandHistory.Count: > 0 } tab)
        {
            return;
        }

        if (!ConfirmDialog.Ask(
                this,
                $"Очистить историю команд этой вкладки ({tab.CommandHistory.Count})?",
                string.Join("\n", tab.CommandHistory.Take(20).Select(t => ShortLineConverter.Shorten(t, 100))),
                "Очистить"))
        {
            return;
        }

        tab.ClearCommandHistory(_request.Section);
        Rebuild();
    }

    // ── правка ──────────────────────────────────────────────────────────────

    /// <param name="into">Во вкладку — для команды, брошенной из истории; иначе в открытую.</param>
    private void StartEdit(CommandSnippet? target, string name, IReadOnlyList<SavedCommandStep> steps, string? into = null)
    {
        _editing = true;
        _target = target;
        _groupBeforeEdit = _group;
        _historyBeforeEdit = _history;
        _history = false;
        _group = target?.Group ?? into ?? _group;
        _draftCount = steps.Count;

        NameBox.Text = name;

        var multi = steps.Count > 1;
        DraftList.ItemsSource = steps
            .Select((s, i) => new DraftStep { Title = $"ОКНО {i + 1}", Text = s.Text, UseSudo = s.UseSudo, ShowHeader = multi })
            .ToList();

        HeaderText.Text = target is null ? "Новая запись" : "Правка записи";
        _editInitial = EditFingerprint();
        ShowEditor(true);
        Rebuild();
        FitDraft();

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        }));
    }

    private void ShowEditor(bool on)
    {
        ListPane.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        EditPane.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        AddButtons.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        SaveButton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.Content = on ? "Отмена" : "Закрыть";
        CloseButton.ToolTip = on ? "Esc" : null;

        // в правке вкладки слева выбирают, куда сохранить запись; там же можно завести новую
        GroupsCaption.Text = on ? "КУДА СОХРАНИТЬ" : "ВКЛАДКИ";
        HistoryPlate.IsEnabled = !on;
    }

    private void FitDraft() =>
        DraftList.Height = Math.Max(DraftScroll.ActualHeight, DraftList.Items.Count * MinDraftHeight);

    private void OnSave(object sender, RoutedEventArgs e) => Save();

    /// <summary>Правка, как была при открытии: название, вкладка, окна и их sudo.</summary>
    private string _editInitial = string.Empty;

    /// <summary>У новой записи с какой-то вкладкой «было» — пустое название: сохранить можно, когда его дали.</summary>
    private string EditFingerprint() => string.Join('\u0001',
        new[] { NameBox.Text.Trim(), _group }
            .Concat((DraftList.ItemsSource as IEnumerable<DraftStep> ?? []).Select(s => $"{s.UseSudo}|{s.Text.Replace("\r\n", "\n").Trim()}")));

    private bool EditChanged() => _editing && EditFingerprint() != _editInitial;

    private void Save()
    {
        if (!EditChanged())
        {
            Toast.Show(AppLog.WarnInPlace(SaveGate.Nothing), SaveGate.Anchor(SaveButton, Toast.Parent as Visual ?? this));
            return;
        }

        var steps = (DraftList.ItemsSource as IEnumerable<DraftStep> ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s.Text))
            .Select(s => new SavedCommandStep { Text = s.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim(), UseSudo = s.UseSudo })
            .ToList();

        if (CommandBook.Save(_target, NameBox.Text.Trim(), _group, steps, _request.Section, _request.Tab, out var saved) is { } refusal)
        {
            Warn(refusal);
            NameBox.Focus();
            return;
        }

        _group = saved!.Group;

        // из истории добавили одну запись — и всё, список избранного не открываем
        if (_quickAdd)
        {
            Close();
            return;
        }

        EndEdit();
    }

    private void EndEdit()
    {
        _editing = false;
        _quickAdd = false;
        _target = null;
        DraftList.ItemsSource = null;
        ShowEditor(false);
        Rebuild();
    }

    private void OnNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Save();
        }
    }

    /// <summary>Esc в правке — отменить её, иначе закрыть окно; Ctrl+Enter в правке — сохранить.</summary>
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;

            // Esc посреди перетаскивания отменяет только его: потеря мыши возвращает всё на место
            if (_grabbed is { List.IsMouseCaptured: true } grabbed)
            {
                grabbed.List.ReleaseMouseCapture();
                return;
            }

            OnClose(sender, e);
        }
        else if (_editing && e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            Save();
        }
    }

    /// <summary>Нижняя кнопка: в правке — «Отмена», иначе — «Закрыть».</summary>
    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (_editing && _quickAdd)
        {
            Close();
        }
        else if (_editing)
        {
            // отмена — обратно туда, что было открыто до правки: вкладка или история
            if (CommandBook.Groups.Contains(_groupBeforeEdit))
            {
                _group = _groupBeforeEdit;
            }

            _history = _historyBeforeEdit;

            EndEdit();
        }
        else
        {
            Close();
        }
    }

    // ── строки списков ──────────────────────────────────────────────────────

    private sealed class GroupRow(string name, int count, bool isSelected) : ObservableObject
    {
        private bool _isDropTarget;

        public string Name { get; } = name;

        public int Count { get; } = count;

        public bool IsSelected { get; } = isSelected;

        /// <summary>Над вкладкой тащат запись — она подсвечена оранжевым.</summary>
        public bool IsDropTarget
        {
            get => _isDropTarget;
            set => Set(ref _isDropTarget, value);
        }

        public override string ToString() => Name;
    }

    private sealed class SnippetRow(CommandSnippet snippet, bool allowMulti)
    {
        public CommandSnippet Snippet { get; } = snippet;

        public string Comment => Snippet.Comment;

        public string Preview => Snippet.Preview;

        public bool IsMulti => Snippet.IsMulti;

        public bool IsPickable => allowMulti || !Snippet.IsMulti;

        public string Badge => IsPickable ? Snippet.MultiText : $"{Snippet.MultiText} — для раздела «Команда»";

        public override string ToString() => Snippet.Comment;
    }

    private sealed class HistoryRow(string text)
    {
        public string Text { get; } = text;

        public string Preview { get; } = ShortLineConverter.Shorten(text);

        public override string ToString() => Text;
    }

    private sealed class DraftStep
    {
        public string Title { get; init; } = string.Empty;

        public string Text { get; set; } = string.Empty;

        public bool UseSudo { get; set; }

        public bool ShowHeader { get; init; }
    }
}
