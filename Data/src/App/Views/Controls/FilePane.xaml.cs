using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MultiServerSync.Core;
using MultiServerSync.Models;
using MultiServerSync.Services;
using MultiServerSync.Views.Dialogs;
using Renci.SshNet.Common;

namespace MultiServerSync.Views.Controls;

/// <summary>Строка списка панели. Объект живёт, пока живёт файл: тихое обновление правит его на месте.</summary>
public sealed class PaneRow : ObservableObject
{
    private FsEntry _entry;
    private bool _isRenaming;
    private string _editName = string.Empty;

    public PaneRow(FsEntry entry, bool isRemote, bool isParent = false, string where = "")
    {
        _entry = entry;
        IsRemote = isRemote;
        IsParent = isParent;
        Where = where;
    }

    public FsEntry Entry
    {
        get => _entry;
        set
        {
            if (Equals(_entry, value))
            {
                return;
            }

            _entry = value;
            RaiseMany(nameof(Name), nameof(Icon), nameof(Overlay), nameof(TypeText), nameof(SizeText), nameof(ModifiedText),
                nameof(Permissions), nameof(OwnerText), nameof(LinkText), nameof(IsHidden));
        }
    }

    public bool IsRemote { get; }

    /// <summary>«..» — строка подъёма на уровень вверх, как в WinSCP.</summary>
    public bool IsParent { get; }

    /// <summary>Найденная поиском папка: где она относительно открытой.</summary>
    public string Where { get; }

    private bool _isCursor;

    /// <summary>Строка под курсором — рамкой; галочку курсор не ставит.</summary>
    public bool IsCursor
    {
        get => _isCursor;
        set => Set(ref _isCursor, value);
    }

    private bool _isDropTarget;

    /// <summary>Над этой папкой держат перетаскиваемое — подсвечена, как в проводнике.</summary>
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set => Set(ref _isDropTarget, value);
    }

    public string Name => _entry.Name;

    public string FullPath => _entry.FullPath;

    public bool IsDirectory => _entry.IsDirectory;

    public bool IsHidden => _entry.IsHidden;

    public long Length => _entry.Length;

    public DateTime Modified => _entry.Modified;

    public string Type => TypeText;

    public string Owner => OwnerText;

    public string LinkTarget => _entry.LinkTarget;

    public ImageSource? Icon => IsParent
        ? ShellIcons.Folder
        : IsRemote
            ? ShellIcons.ForName(_entry.Name, _entry.IsDirectory, _entry.IsExecutable)
            : ShellIcons.ForLocal(_entry.FullPath, _entry.IsDirectory, _entry.Kind == FsKind.Drive);

    public ImageSource? Overlay => _entry.IsLink ? ShellIcons.LinkOverlay : null;

    public string TypeText => IsParent
        ? string.Empty
        : _entry.Type ?? (_entry.IsLink && !IsRemote
            ? "Ссылка"
            : ShellIcons.TypeName(_entry.Name, _entry.IsDirectory, _entry.IsExecutable));

    public string SizeText => IsParent || _entry.IsDirectory && _entry.Kind != FsKind.Drive
        ? string.Empty
        : _entry.Kind == FsKind.Drive && _entry.Length == 0 ? string.Empty : TransferItem.Format(_entry.Length);

    public string ModifiedText => !IsParent && _entry.Modified.Year > 1970 ? _entry.Modified.ToString("dd.MM.yyyy HH:mm") : string.Empty;

    public string Permissions => IsParent ? string.Empty : _entry.Permissions;

    public string OwnerText => IsParent || _entry.Owner.Length == 0
        ? string.Empty
        : _entry.Group.Length == 0 || _entry.Group == _entry.Owner ? _entry.Owner : $"{_entry.Owner}:{_entry.Group}";

    public string LinkText => _entry.IsLink && _entry.LinkTarget.Length > 0 ? "→ " + _entry.LinkTarget : string.Empty;

    public bool IsRenaming
    {
        get => _isRenaming;
        set
        {
            if (Set(ref _isRenaming, value))
            {
                Raise(nameof(ShowsName));
            }
        }
    }

    public bool ShowsName => !_isRenaming;

    public string EditName
    {
        get => _editName;
        set => Set(ref _editName, value ?? string.Empty);
    }
}

/// <summary>Узел дерева папок панели. Дети читаются при раскрытии.</summary>
public sealed class PaneNode : ObservableObject
{
    private bool _isExpanded;
    private bool _isSelected;

    public PaneNode(string name, string fullPath, ImageSource? icon, bool placeholder = false)
    {
        Name = name;
        FullPath = fullPath;
        Icon = icon;
        IsPlaceholder = placeholder;
        Children = placeholder ? [] : [new PaneNode("…", string.Empty, null, placeholder: true)];
    }

    public string Name { get; }

    public string FullPath { get; }

    public ImageSource? Icon { get; }

    public bool IsPlaceholder { get; }

    public bool IsLoaded { get; set; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public ObservableCollection<PaneNode> Children { get; }
}

/// <summary>Что панель просит у страницы проводника — то, что касается обеих панелей или всей программы.</summary>
public enum PaneCommand
{
    Copy,
    Cut,
    Paste,
    Open,
    OpenWith,
    Properties,
    Compare,
    Run,
    CommandTabHere,
    SendToCopy,
    SendToDownload,
    TransferToOther,
    TransferOptions,
}

/// <summary>Перетащили в панель: из соседней панели или из проводника Windows.</summary>
public sealed record PaneDrop(FilePane? From, IReadOnlyList<FsEntry> Items, IReadOnlyList<string> WindowsFiles, string TargetDir, bool Move);

/// <summary>
/// Панель проводника — одна на компьютер и сервер: адрес хлебными крошками, назад/вперёд/вверх,
/// маски, дерево, список с системными значками, клавиши как в Windows. Что именно показывать
/// и как удалять, знает источник (<see cref="IFileSource"/>), панель про разницу не знает.
/// </summary>
public partial class FilePane : UserControl
{
    private readonly Stack<string> _back = new();
    private readonly Stack<string> _forward = new();
    private readonly ObservableCollection<PaneRow> _rows = new();
    private readonly Dictionary<string, string> _focusByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _watchDelay;
    private readonly DispatcherTimer _searchDelay;

    private ListCollectionView? _view;
    private IFileSource? _source;
    private ExplorerPaneState _state = new();
    private string _current = string.Empty;
    private int _navVersion;
    private bool _busy;
    private bool _searching;
    private CancellationTokenSource? _searchCts;
    private FileSystemWatcher? _watcher;
    private DateTime _freeAt;
    private long? _free;
    private PaneNode? _treeRoot;
    private PaneNode? _treeSelected;
    private bool _syncingTree;
    private string _typed = string.Empty;
    private DateTime _typedAt;
    private string? _restoringFocus;

    /// <summary>
    /// Выделение — это выбор человека: программа его не ставит, а помнит. По папкам — пока
    /// открыта программа, для открытой папки — и в файле вкладки.
    /// </summary>
    private readonly Dictionary<string, List<string>> _selectionByPath = new(StringComparer.OrdinalIgnoreCase);

    private List<string>? _restoringSelection;

    /// <summary>Строки меняет сама программа (переход, поиск, маска) — это не выбор человека.</summary>
    private int _selectionHold;

    public FilePane()
    {
        InitializeComponent();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += async (_, _) => await QuietRefreshAsync();

        _watchDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _watchDelay.Tick += async (_, _) =>
        {
            _watchDelay.Stop();
            await QuietRefreshAsync(force: true);
        };

        _searchDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _searchDelay.Tick += async (_, _) =>
        {
            _searchDelay.Stop();
            await SearchFoldersAsync();
        };

        // столбцы — по ширине панели: новые строки могут быть длиннее, поэтому мерить заново
        _fitDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _fitDelay.Tick += (_, _) =>
        {
            _fitDelay.Stop();
            MeasureColumns();
            FitColumns();
        };

        ((System.Collections.Specialized.INotifyCollectionChanged)List.Items).CollectionChanged += (_, _) => ScheduleFit();
        List.SizeChanged += (_, _) => FitColumns();
        List.Loaded += (_, _) =>
        {
            // полоса прокрутки появилась или пропала — места под столбцы стало другое
            if (FindChild<ScrollViewer>(List) is { } scroll)
            {
                scroll.ScrollChanged -= OnListScrollChanged;
                scroll.ScrollChanged += OnListScrollChanged;
            }

            ScheduleFit();
        };

        List.CurrentCellChanged += OnCursorMoved;
        List.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnColumnResized));

        PreviewKeyDown += OnPaneKeyDown;
        GotKeyboardFocus += (_, _) => Activated?.Invoke(this);
        PreviewMouseDown += (_, _) => Activated?.Invoke(this);
        IsVisibleChanged += (_, _) => UpdateTimers();
        Unloaded += (_, _) => StopWatching();
    }

    /// <summary>Панелью начали пользоваться: щелчок, клавиши. Страница делает её активной.</summary>
    public event Action<FilePane>? Activated;

    public event Action<FilePane, PaneCommand>? CommandRequested;

    public event Action<FilePane, PaneDrop>? DropRequested;

    /// <summary>Щелчок по названию панели — когда оно выбирает, чьи это файлы (<see cref="EnableTitlePicker"/>).</summary>
    public event Action<FilePane, FrameworkElement>? TitleClicked;

    /// <summary>Название панели становится кнопкой со стрелкой: из него выбирают сервер.</summary>
    public void EnableTitlePicker(string tip)
    {
        TitleButton.IsHitTestVisible = true;
        TitleButton.ToolTip = tip;
        TitleChevron.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Кнопка «Копировать» в соседнюю панель и параметры копирования рядом.
    /// Стрелка смотрит туда, куда пойдут файлы.
    /// </summary>
    public void ShowTransfer(bool toRight, string tip)
    {
        TransferButton.Icon = new Wpf.Ui.Controls.SymbolIcon(toRight
            ? Wpf.Ui.Controls.SymbolRegular.ArrowRight24
            : Wpf.Ui.Controls.SymbolRegular.ArrowLeft24);
        TransferButton.ToolTip = tip;
        TransferButton.Visibility = Visibility.Visible;
        OptionsButton.Visibility = Visibility.Visible;
    }

    /// <summary>Есть что копировать и куда.</summary>
    public bool CanTransfer
    {
        set => TransferButton.IsEnabled = value;
    }

    private void OnTitleClick(object sender, RoutedEventArgs e) => TitleClicked?.Invoke(this, TitleButton);

    private void OnTransfer(object sender, RoutedEventArgs e) => CommandRequested?.Invoke(this, PaneCommand.TransferToOther);

    private void OnTransferOptions(object sender, RoutedEventArgs e) => CommandRequested?.Invoke(this, PaneCommand.TransferOptions);

    /// <summary>Открыта другая папка, сменился курсор или сортировка — страница сохраняет вкладку.</summary>
    public event Action<FilePane>? StateChanged;

    public IFileSource? Source => _source;

    public bool IsRemote => _source?.IsRemote == true;

    public string CurrentPath => _current;

    /// <summary>Отмеченные галочками строки без «..».</summary>
    public IReadOnlyList<FsEntry> SelectedEntries => List.SelectedItems
        .OfType<PaneRow>()
        .Where(r => !r.IsParent && r.Entry.Kind == FsKind.Normal)
        .Select(r => r.Entry)
        .ToList();

    /// <summary>Строка под курсором. Курсор и галочка — разные вещи: щелчок по строке её не отмечает.</summary>
    public FsEntry? FocusedEntry => CursorRow is { IsParent: false } row ? row.Entry : null;

    private PaneRow? CursorRow => List.CurrentItem as PaneRow;

    /// <summary>Пока открыто меню правой кнопки — то, по чему щёлкнули; см. <see cref="TargetEntries"/>.</summary>
    private IReadOnlyList<FsEntry>? _menuTargets;

    /// <summary>
    /// С чем работает действие: из меню правой кнопки — строка, по которой щёлкнули (а если
    /// она отмечена — все отмеченные); иначе отмеченное галочками, а без галочек — строка под курсором.
    /// </summary>
    public IReadOnlyList<FsEntry> TargetEntries =>
        _menuTargets ?? (SelectedEntries is { Count: > 0 } picked ? picked : FocusedEntry is { Kind: FsKind.Normal } one ? [one] : []);

    /// <summary>Курсор на строку: рамкой, без галочки, и клавиши — в неё.</summary>
    private void MoveCursor(PaneRow row)
    {
        List.CurrentCell = new DataGridCellInfo(row, NameColumn);
        List.ScrollIntoView(row);
        FocusRow(row);
    }

    /// <summary>
    /// Фокус клавиатуры — в ячейку имени строки. Сама строка таблицы фокус не берёт: раньше он
    /// оставался в соседней панели, и F2/Del/Enter после щелчка срабатывали там.
    /// </summary>
    private void FocusRow(object row)
    {
        if (List.ItemContainerGenerator.ContainerFromItem(row) is not DataGridRow)
        {
            List.UpdateLayout();
        }

        if (List.ItemContainerGenerator.ContainerFromItem(row) is DataGridRow generated
            && NameColumn.GetCellContent(generated)?.Parent is DataGridCell cell
            && cell.Focus())
        {
            return;
        }

        List.Focus();
    }

    /// <summary>Рамка курсора идёт за текущей строкой таблицы.</summary>
    private void OnCursorMoved(object? sender, EventArgs e)
    {
        foreach (var row in _rows.Where(r => r.IsCursor && !ReferenceEquals(r, CursorRow)))
        {
            row.IsCursor = false;
        }

        if (CursorRow is { } current)
        {
            current.IsCursor = true;

            if (FocusedEntry is { } entry && _state.Focus != entry.Name)
            {
                _state.Focus = entry.Name;
                StateChanged?.Invoke(this);
            }
        }
    }

    /// <summary>Все строки открытой папки — для проверки, что уже лежит в назначении.</summary>
    public IReadOnlyList<FsEntry> Entries => _rows.Where(r => !r.IsParent).Select(r => r.Entry).ToList();

    /// <summary>Источник «никакой»: сервер не выбран. Панель пустая, посредине — что делать.</summary>
    public void ShowEmpty(string title, ImageSource? icon, string message)
    {
        StopWatching();
        _source = null;
        _navVersion++;
        TitleText.Text = title;
        TitleIcon.Source = icon;
        TitleText.Visibility = TitleIcon.Visibility = TitleButton.Visibility = Visibility.Visible;
        WindowsExplorerButton.Visibility = Visibility.Collapsed;
        _rows.Clear();
        Crumbs.Children.Clear();
        ShowMessage(message);
        StatusText.Text = string.Empty;
        ResetTree();
    }

    /// <summary>Подключает панель к источнику и памяти вкладки и открывает запомненную папку.</summary>
    public async Task AttachAsync(IFileSource source, ExplorerPaneState state, string title, ImageSource? icon)
    {
        _selectionHold++;

        try
        {
            await AttachCoreAsync(source, state, title, icon);
        }
        finally
        {
            _selectionHold--;
        }
    }

    private async Task AttachCoreAsync(IFileSource source, ExplorerPaneState state, string title, ImageSource? icon)
    {
        StopWatching();
        CancelSearch();

        _restoringSelection = state.Selected.ToList();
        _selectionByPath.Clear();
        _source = source;
        _state = state;
        _back.Clear();
        _forward.Clear();
        _focusByPath.Clear();
        _rows.Clear();
        _free = null;

        TitleText.Text = title;
        TitleIcon.Source = icon;
        FileMaskBox.Text = state.FileMask;
        FolderMaskBox.Text = string.Empty;
        ApplyColumns();
        ShowTree(state.ShowTree, save: false);
        ResetTree();

        _restoringFocus = state.Focus;

        // папка не помнится — открывается домашняя, а её сервер называет только после входа
        if (source.IsRemote && state.Path.Length == 0)
        {
            ShowMessage("подключение…");
            StatusText.Text = "подключение…";

            try
            {
                await source.PrepareAsync();
            }
            catch (Exception ex)
            {
                if (ReferenceEquals(source, _source))
                {
                    ShowMessage("Не удалось подключиться: " + ex.Message);
                    StatusText.Text = "нет связи с сервером";
                    Log("Не удалось подключиться: " + ex.Message, error: true);
                }

                return;
            }

            if (!ReferenceEquals(source, _source))
            {
                return;
            }
        }

        // папку могли удалить — тогда ближайшая выше, как при первом обзоре в копировании
        var target = source.Normalize(state.Path);

        while (true)
        {
            var outcome = await NavigateAsync(target, record: false);

            if (outcome != NavResult.Missing || source.Parent(target) is not { } parent || !ReferenceEquals(source, _source))
            {
                break;
            }

            target = parent;
        }

        UpdateTimers();
    }

    /// <summary>Рамка цветом акцента — у той панели, куда пойдут клавиши.</summary>
    public void SetActive(bool active)
    {
        Frame.BorderBrush = active
            ? (Brush)FindResource("AccentFillColorDefaultBrush")
            : (Brush)FindResource("ControlStrokeColorDefaultBrush");
    }

    /// <summary>Клавиши — в список, на строку под курсором. Выделение не трогается.</summary>
    public void FocusList()
    {
        var item = List.CurrentItem ?? (_view?.Count > 0 ? _view.GetItemAt(0) : null);

        if (item is not null && List.CurrentItem is null)
        {
            List.CurrentCell = new DataGridCellInfo(item, NameColumn);
        }

        if (item is not null)
        {
            FocusRow(item);
            return;
        }

        List.Focus();
    }

    // ── открытие папки ────────────────────────────────────────────────────────

    private enum NavResult
    {
        Ok,
        Missing,
        Failed,
        Stale,
    }

    public Task RefreshAsync() => NavigateAsync(_current, record: false, refresh: true);

    private async Task<NavResult> NavigateAsync(string path, bool record = true, bool refresh = false, string? focus = null)
    {
        if (_source is not { } source)
        {
            return NavResult.Failed;
        }

        var target = source.Normalize(path);
        var version = ++_navVersion;

        CancelSearch();
        RememberFocus();

        // из кэша — сразу, без похода на сервер; давно прочитанное тихо перечитывается следом
        var cached = refresh ? null : source.Cached(target);
        var revalidate = cached is not null && !source.IsFresh(target);
        IReadOnlyList<FsEntry> items;

        if (cached is not null)
        {
            items = cached;
        }
        else
        {
            StatusText.Text = source.IsRemote ? $"чтение {target} …" : "чтение…";
            _busy = true;

            try
            {
                items = await source.ListAsync(target, refresh);
            }
            catch (Exception ex)
            {
                if (version != _navVersion || !ReferenceEquals(source, _source))
                {
                    return NavResult.Stale;
                }

                if (ex is SftpPathNotFoundException or DirectoryNotFoundException)
                {
                    StatusText.Text = $"Папки {source.Display(target)} нет.";
                    return NavResult.Missing;
                }

                var what = ex is SftpPermissionDeniedException or UnauthorizedAccessException
                    ? $"Нет прав на чтение {source.Display(target)}."
                    : IsConnectionProblem(ex) ? "Нет связи с сервером: " + ex.Message : "Ошибка: " + ex.Message;

                // список из кэша остаётся на месте: связь пропала — это не повод опустошать панель
                if (_rows.Count == 0)
                {
                    ShowMessage(what);
                }

                StatusText.Text = what;
                Log(what, error: true);
                return NavResult.Failed;
            }
            finally
            {
                _busy = false;
            }
        }

        if (version != _navVersion || !ReferenceEquals(source, _source))
        {
            return NavResult.Stale;
        }

        if (record && !string.Equals(_current, target, StringComparison.Ordinal))
        {
            _back.Push(_current);
            _forward.Clear();
        }

        var cameFrom = _current;
        var keep = _restoringSelection ?? _selectionByPath.GetValueOrDefault(target) ?? [];
        _restoringSelection = null;
        _selectionHold++;

        try
        {
            _current = target;
            ShowRows(items, refill: true);
            RestoreSelection(keep);
        }
        finally
        {
            _selectionHold--;
        }

        ShowPath(target);
        UpdateNavButtons();
        Watch(target);
        PushRecent(target);

        // курсор: что просили, что помнили для этой папки, а при подъёме — папка, откуда пришли
        var wanted = focus
            ?? _restoringFocus
            ?? _focusByPath.GetValueOrDefault(target)
            ?? (source.Parent(cameFrom) == target ? Path.GetFileName(cameFrom.TrimEnd('/', '\\')) : null);

        _restoringFocus = null;
        PlaceCursor(wanted);

        _state.Path = target;
        _state.Selected = keep.Where(n => _rows.Any(r => !r.IsParent && r.Name == n)).ToList();
        StateChanged?.Invoke(this);

        _ = UpdateStatusAsync(target, version);
        _ = RevealInTreeAsync(target, version);

        if (source.IsRemote)
        {
            // подпапки — заранее: щелчок по любой покажет её сразу
            var dirs = items.Where(i => i.IsDirectory).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .Take(300).Select(i => i.FullPath).ToList();
            _ = source.PrefetchAsync(dirs);
        }

        if (revalidate)
        {
            _ = QuietRefreshAsync(force: true);
        }

        return NavResult.Ok;
    }

    private static bool IsConnectionProblem(Exception ex) =>
        ex is SshConnectionException or System.Net.Sockets.SocketException or SshOperationTimeoutException
        || ex.InnerException is System.Net.Sockets.SocketException;

    /// <summary>
    /// Тихое обновление открытой папки: раз в несколько секунд на сервере, по сигналу Windows
    /// на компьютере. Строки правятся на месте — выделение, курсор и прокрутка не сбиваются.
    /// </summary>
    private async Task QuietRefreshAsync(bool force = false)
    {
        if (_source is not { } source || _busy || _searching || _rows.Any(r => r.IsRenaming))
        {
            return;
        }

        // на сервер ходим, только когда панель видна и окно в работе
        if (!force && (!IsVisible || Window.GetWindow(this) is not { IsActive: true } || !source.IsRemote))
        {
            return;
        }

        var path = _current;
        var version = _navVersion;
        IReadOnlyList<FsEntry> fresh;

        try
        {
            _busy = true;
            fresh = await source.ListAsync(path, refresh: true);
        }
        catch (Exception ex)
        {
            if (IsConnectionProblem(ex) && version == _navVersion)
            {
                StatusText.Text = "нет связи с сервером — показано прочитанное раньше";
            }

            return;
        }
        finally
        {
            _busy = false;
        }

        if (version != _navVersion || !ReferenceEquals(source, _source) || path != _current)
        {
            return;
        }

        ShowRows(fresh, refill: false);
        _ = UpdateStatusAsync(path, version);
    }

    /// <summary>
    /// Список папки. Заново (переход) — строки пересоздаются; на месте (обновление) — та же
    /// строка остаётся тем же объектом, меняются только её поля.
    /// </summary>
    private void ShowRows(IReadOnlyList<FsEntry> items, bool refill)
    {
        if (_source is not { } source)
        {
            return;
        }

        HideMessage();
        WhereColumn.Visibility = Visibility.Collapsed;

        if (refill || _view is null || !ReferenceEquals(List.ItemsSource, _view))
        {
            _rows.Clear();

            if (source.Parent(_current) is { } parent)
            {
                _rows.Add(new PaneRow(new FsEntry("..", parent, true, 0, default), source.IsRemote, isParent: true));
            }

            foreach (var item in items)
            {
                _rows.Add(new PaneRow(item, source.IsRemote));
            }

            _view = new ListCollectionView(_rows)
            {
                CustomSort = new RowOrder(_state.SortKey, _state.SortDescending),
                Filter = FileFilter,
            };

            List.ItemsSource = _view;
        }
        else
        {
            var byName = items.ToDictionary(i => i.Name, StringComparer.Ordinal);

            for (var i = _rows.Count - 1; i >= 0; i--)
            {
                var row = _rows[i];

                if (row.IsParent)
                {
                    continue;
                }

                if (byName.Remove(row.Name, out var entry))
                {
                    row.Entry = entry;
                }
                else
                {
                    _rows.RemoveAt(i);
                }
            }

            foreach (var entry in byName.Values)
            {
                _rows.Add(new PaneRow(entry, source.IsRemote));
            }
        }

        ShowSortArrow();
        UpdateLinkColumn();

        if (_rows.Count == 0 || _rows.All(r => r.IsParent))
        {
            ShowMessage("Папка пуста");
        }
    }

    private void UpdateLinkColumn() =>
        LinkColumn.Visibility = IsRemote && _rows.Any(r => r.Entry.IsLink) && !IsHidden("Куда ведёт")
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void ShowMessage(string text)
    {
        EmptyText.Text = text;
        EmptyText.Visibility = Visibility.Visible;
    }

    private void HideMessage() => EmptyText.Visibility = Visibility.Collapsed;

    /// <summary>Строка состояния: сколько в папке, сколько выбрано, свободно на диске.</summary>
    private async Task UpdateStatusAsync(string path, int version)
    {
        WriteStatus();

        if (_source is not { } source || DateTime.Now - _freeAt < TimeSpan.FromSeconds(30) && _free is not null)
        {
            return;
        }

        _freeAt = DateTime.Now;
        var free = await source.FreeSpaceAsync(path);

        if (version == _navVersion && ReferenceEquals(source, _source))
        {
            _free = free;
            WriteStatus();
        }
    }

    private void WriteStatus()
    {
        if (_searching)
        {
            return;
        }

        var all = _rows.Count(r => !r.IsParent);
        var shown = _view?.Cast<PaneRow>().Count(r => !r.IsParent) ?? all;
        var picked = List.SelectedItems.OfType<PaneRow>().Where(r => !r.IsParent).ToList();

        var text = shown == all ? $"элементов: {all}" : $"показано {shown} из {all}";

        if (picked.Count > 0)
        {
            var bytes = picked.Where(r => !r.IsDirectory).Sum(r => r.Length);
            text += $" · выбрано {picked.Count}" + (bytes > 0 ? $" ({TransferItem.Format(bytes)})" : string.Empty);
        }

        if (_free is { } free)
        {
            text += $" · свободно {TransferItem.Format(free)}";
        }

        StatusText.Text = text;
    }

    private void OnListSelected(object sender, SelectionChangedEventArgs e)
    {
        WriteStatus();
        UpdateAllCheck();
        RememberSelection();
    }

    private void RememberFocus()
    {
        if (_current.Length > 0 || _source is { IsRemote: false })
        {
            if (FocusedEntry is { } entry)
            {
                _focusByPath[_current] = entry.Name;
            }
        }
    }

    /// <summary>Курсор на строку с этим именем (нет такой — на первую), но без выделения: галочки ставит человек.</summary>
    private void PlaceCursor(string? name)
    {
        if (_view is null)
        {
            return;
        }

        var row = name is null ? null : _view.Cast<PaneRow>().FirstOrDefault(r => r.Name == name);
        row ??= _view.Cast<PaneRow>().FirstOrDefault();

        if (row is not null)
        {
            List.CurrentCell = new DataGridCellInfo(row, NameColumn);
            List.ScrollIntoView(row);
        }
    }

    /// <summary>Выбор человека в открытой папке — в память панели и вкладки.</summary>
    private void RememberSelection()
    {
        if (_selectionHold > 0 || _searching || _source is null || !ReferenceEquals(List.ItemsSource, _view))
        {
            return;
        }

        var names = List.SelectedItems.OfType<PaneRow>().Where(r => !r.IsParent).Select(r => r.Name).ToList();
        _selectionByPath[_current] = names;

        if (!_state.Selected.SequenceEqual(names))
        {
            _state.Selected = names;
            StateChanged?.Invoke(this);
        }
    }

    /// <summary>Отметить запомненное заново — из того, что сейчас видно в списке.</summary>
    private void RestoreSelection(IReadOnlyCollection<string> names)
    {
        List.UnselectAll();

        if (_view is null || names.Count == 0)
        {
            return;
        }

        var set = names.ToHashSet(StringComparer.Ordinal);

        foreach (var row in _view.Cast<PaneRow>().Where(r => !r.IsParent && set.Contains(r.Name)))
        {
            List.SelectedItems.Add(row);
        }
    }

    /// <summary>Выделить несколько строк по именам — после вставки и переноса.</summary>
    public void SelectNames(IReadOnlyCollection<string> names)
    {
        if (_view is null || names.Count == 0)
        {
            return;
        }

        var set = names.ToHashSet(StringComparer.Ordinal);
        List.SelectedItems.Clear();

        foreach (var row in _view.Cast<PaneRow>().Where(r => set.Contains(r.Name)))
        {
            List.SelectedItems.Add(row);
        }

        if (List.SelectedItems.Count > 0)
        {
            List.ScrollIntoView(List.SelectedItems[0]);
        }
    }

    // ── слежение за папкой на компьютере ──────────────────────────────────────

    private void Watch(string path)
    {
        StopWatching();

        if (_source is not { IsRemote: false } || path.Length == 0 || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };

            FileSystemEventHandler changed = (_, _) => Dispatcher.BeginInvoke(() =>
            {
                _watchDelay.Stop();
                _watchDelay.Start();
            });

            _watcher.Changed += changed;
            _watcher.Created += changed;
            _watcher.Deleted += changed;
            _watcher.Renamed += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                _watchDelay.Stop();
                _watchDelay.Start();
            });
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception)
        {
            // сетевая папка без уведомлений — обновится по F5
            StopWatching();
        }
    }

    private void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    private void UpdateTimers()
    {
        if (IsVisible && _source is { IsRemote: true })
        {
            _refreshTimer.Start();
        }
        else
        {
            _refreshTimer.Stop();
        }
    }

    // ── маски ─────────────────────────────────────────────────────────────────

    private bool FileFilter(object row) =>
        row is not PaneRow item || item.IsParent || item.IsDirectory || NameMask.Matches(item.Name, FileMaskBox.Text);

    private void OnFileMaskChanged(object sender, TextChangedEventArgs e)
    {
        FileMaskClear.Visibility = NameMask.IsEmpty(FileMaskBox.Text) ? Visibility.Collapsed : Visibility.Visible;

        // маска прячет строки, но не снимает выбор: сняли маску — отмеченное снова отмечено
        _selectionHold++;

        try
        {
            _view?.Refresh();
            RestoreSelection(_selectionByPath.GetValueOrDefault(_current) ?? _state.Selected);
        }
        finally
        {
            _selectionHold--;
        }

        WriteStatus();

        if (_state.FileMask != FileMaskBox.Text)
        {
            _state.FileMask = FileMaskBox.Text;
            StateChanged?.Invoke(this);
        }
    }

    private void OnClearFileMask(object sender, RoutedEventArgs e) => FileMaskBox.Text = string.Empty;

    private void OnFolderMaskChanged(object sender, TextChangedEventArgs e)
    {
        FolderMaskClear.Visibility = NameMask.IsEmpty(FolderMaskBox.Text) ? Visibility.Collapsed : Visibility.Visible;
        _searchDelay.Stop();

        if (NameMask.IsEmpty(FolderMaskBox.Text))
        {
            EndSearch();
            return;
        }

        _searchDelay.Start();
    }

    private async void OnFolderMaskKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _searchDelay.Stop();
            await SearchFoldersAsync();
            FocusList();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            FolderMaskBox.Text = string.Empty;
            FocusList();
        }
    }

    private void OnClearFolderMask(object sender, RoutedEventArgs e) => FolderMaskBox.Text = string.Empty;

    /// <summary>Поиск папок вглубь: на сервере одной командой find, здесь — обходом в фоне.</summary>
    private async Task SearchFoldersAsync()
    {
        if (_source is not { } source || NameMask.IsEmpty(FolderMaskBox.Text) || _current.Length == 0 && !source.IsRemote)
        {
            return;
        }

        CancelSearch();
        var cts = _searchCts = new CancellationTokenSource();
        var mask = FolderMaskBox.Text;
        var root = _current;
        var started = DateTime.Now;

        _searching = true;
        StatusText.Text = "ищу папки…";

        try
        {
            var found = await source.FindFoldersAsync(root, mask, depth: 6, limit: 2000, cts.Token);

            if (cts.IsCancellationRequested || root != _current)
            {
                return;
            }

            var rows = new ObservableCollection<PaneRow>(found.Select(f => new PaneRow(f, source.IsRemote, where: Relative(root, f.FullPath))));
            var view = new ListCollectionView(rows) { CustomSort = new RowOrder("Where", false) };

            List.ItemsSource = view;
            WhereColumn.Visibility = Visibility.Visible;
            HideMessage();

            if (rows.Count == 0)
            {
                ShowMessage($"Папок «{mask}» не найдено");
            }

            var seconds = (DateTime.Now - started).TotalSeconds;
            StatusText.Text = $"найдено папок: {rows.Count}{(rows.Count >= 2000 ? " (показаны первые 2000)" : string.Empty)} за {seconds:0.0} с · Enter — перейти";
        }
        catch (OperationCanceledException)
        {
            // начали новый поиск или ушли
        }
        catch (Exception ex)
        {
            StatusText.Text = "поиск не удался: " + ex.Message;
        }
    }

    private string Relative(string root, string path)
    {
        var parent = _source?.Parent(path) ?? string.Empty;

        if (parent.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            parent = parent[root.Length..].TrimStart('/', '\\');
        }

        return parent.Length == 0 ? "." : parent;
    }

    private void CancelSearch()
    {
        _searchCts?.Cancel();
        _searchCts = null;
    }

    /// <summary>Поиск сняли — список открытой папки возвращается на место.</summary>
    private void EndSearch()
    {
        CancelSearch();

        if (!_searching)
        {
            return;
        }

        _searching = false;
        WhereColumn.Visibility = Visibility.Collapsed;
        _selectionHold++;

        try
        {
            List.ItemsSource = _view;
            RestoreSelection(_selectionByPath.GetValueOrDefault(_current) ?? _state.Selected);
        }
        finally
        {
            _selectionHold--;
        }

        HideMessage();
        WriteStatus();
    }

    // ── сортировка ───────────────────────────────────────────────────────────

    private void OnListSorting(object sender, DataGridSortingEventArgs e)
    {
        // своя сортировка: «..» всегда сверху и папки перед файлами при любом столбце
        e.Handled = true;

        var key = e.Column.SortMemberPath;

        _state.SortDescending = key == _state.SortKey && !_state.SortDescending;
        _state.SortKey = key;

        if (List.ItemsSource is ListCollectionView view)
        {
            view.CustomSort = new RowOrder(key, _state.SortDescending);
        }

        ShowSortArrow();
        StateChanged?.Invoke(this);
    }

    private void ShowSortArrow()
    {
        foreach (var column in List.Columns)
        {
            column.SortDirection = column.SortMemberPath == _state.SortKey
                ? _state.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending
                : null;
        }
    }

    private sealed class RowOrder(string key, bool descending) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not PaneRow a || y is not PaneRow b)
            {
                return 0;
            }

            if (a.IsParent != b.IsParent)
            {
                return a.IsParent ? -1 : 1;
            }

            // в «Этом компьютере» быстрые места выше дисков — как в проводнике
            if (a.Entry.Kind != b.Entry.Kind)
            {
                return a.Entry.Kind.CompareTo(b.Entry.Kind) * (a.Entry.Kind == FsKind.Normal ? 1 : -1);
            }

            if (a.IsDirectory != b.IsDirectory)
            {
                return a.IsDirectory ? -1 : 1;
            }

            var order = key switch
            {
                "Length" => a.Length.CompareTo(b.Length),
                "Modified" => a.Modified.CompareTo(b.Modified),
                "Type" => string.Compare(a.TypeText, b.TypeText, StringComparison.CurrentCultureIgnoreCase),
                "Permissions" => string.CompareOrdinal(a.Permissions, b.Permissions),
                "Owner" => string.Compare(a.OwnerText, b.OwnerText, StringComparison.OrdinalIgnoreCase),
                "LinkTarget" => string.CompareOrdinal(a.LinkTarget, b.LinkTarget),
                "Where" => string.Compare(a.Where, b.Where, StringComparison.OrdinalIgnoreCase),
                _ => 0,
            };

            if (order == 0)
            {
                order = NaturalCompare(a.Name, b.Name);

                // по имени при другом ключе — всегда по возрастанию, как в проводнике
                if (key != "Name")
                {
                    return order;
                }
            }

            return descending ? -order : order;
        }

        /// <summary>«file2» раньше «file10» — как сортирует проводник Windows.</summary>
        private static int NaturalCompare(string a, string b) => StrCmpLogicalW(a, b);

        [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int StrCmpLogicalW(string x, string y);
    }

    // ── столбцы ──────────────────────────────────────────────────────────────

    private bool IsHidden(string header) => _state.HiddenColumns.Contains(header);

    /// <summary>Права, владелец и ссылки — только у сервера; скрытые человеком — скрыты.</summary>
    private void ApplyColumns()
    {
        // у компьютера название не нужно — и так понятно, чьи это файлы
        WindowsExplorerButton.Visibility = IsRemote ? Visibility.Collapsed : Visibility.Visible;
        TitleIcon.Visibility = IsRemote ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Visibility = TitleIcon.Visibility;
        TitleButton.Visibility = TitleIcon.Visibility;

        foreach (var column in List.Columns)
        {
            var header = column.Header as string ?? string.Empty;

            if (header is "Имя" or "Где" || column == CheckColumn)
            {
                continue;
            }

            var remoteOnly = header is "Права" or "Владелец" or "Куда ведёт";
            column.Visibility = (remoteOnly && !IsRemote) || IsHidden(header) ? Visibility.Collapsed : Visibility.Visible;
        }

        UpdateLinkColumn();
        ScheduleFit();
    }

    // ── ширина столбцов ──────────────────────────────────────────────────────

    private readonly DispatcherTimer _fitDelay;

    /// <summary>Ширина столбца по содержимому — сколько ему нужно, чтобы ничего не обрезать.</summary>
    private readonly Dictionary<DataGridColumn, double> _natural = new();

    private void ScheduleFit()
    {
        _fitDelay.Stop();
        _fitDelay.Start();
    }

    private void OnListScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportWidthChange != 0)
        {
            FitColumns();
        }
    }

    private void MeasureColumns()
    {
        var dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var rows = List.Items.OfType<PaneRow>().ToList();

        foreach (var column in List.Columns)
        {
            if (column == CheckColumn)
            {
                continue;
            }

            Func<PaneRow, string> text = column == NameColumn
                ? r => r.Name
                : column is DataGridTextColumn { Binding: Binding { Path.Path: var path } } && typeof(PaneRow).GetProperty(path) is { } property
                    ? r => property.GetValue(r) as string ?? string.Empty
                    : _ => string.Empty;

            var font = column == RightsColumn ? new FontFamily("Consolas") : List.FontFamily;

            // ширину дают самые длинные строки — меряются несколько самых длинных, а не все тысячи
            var cells = rows.Select(text)
                .Where(t => t.Length > 0)
                .Distinct()
                .OrderByDescending(t => t.Length)
                .Take(5)
                .Select(t => TextWidth(t, font, 12, FontWeights.Normal, dip))
                .DefaultIfEmpty(0)
                .Max();

            // у шапки — место под стрелку сортировки, у имени — под значок
            var head = TextWidth(column.Header as string ?? string.Empty, List.FontFamily, 11, FontWeights.SemiBold, dip) + 14;
            var icon = column == NameColumn ? 24 : 0;

            _natural[column] = Math.Ceiling(Math.Max(cells + icon, head) + 18);
        }
    }

    private static double TextWidth(string text, FontFamily font, double size, FontWeight weight, double dip) =>
        new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(font, FontStyles.Normal, weight, FontStretches.Normal), size, Brushes.Black, dip).WidthIncludingTrailingWhitespace;

    private static string HeaderOf(DataGridColumn column) => column.Header as string ?? string.Empty;

    /// <summary>Ширина, которую человек дал руками; NaN — по содержимому.</summary>
    private double UserWidth(DataGridColumn column) =>
        _state.Columns.TryGetValue(HeaderOf(column), out var width) && width > 0 ? width : double.NaN;

    /// <summary>
    /// Столбец, растянутый руками за границу в шапке, держит свою ширину — она запоминается во
    /// вкладке. Остальные — по содержимому, чтобы дата и размер не обрезались. Не хватает места —
    /// сначала ужимается «Имя» до своего минимума (у длинного имени есть подсказка), и только
    /// потом поровну остальные столбцы «по содержимому». Места с запасом — остаток отдаётся имени.
    /// </summary>
    private void FitColumns()
    {
        if (_natural.Count == 0 || FindChild<ScrollViewer>(List) is not { } scroll)
        {
            return;
        }

        var width = scroll.ViewportWidth > 0 ? scroll.ViewportWidth : List.ActualWidth;
        var room = width - CheckColumn.Width.DisplayValue - 1;

        if (room <= 0)
        {
            return;
        }

        var visible = List.Columns.Where(c => c != CheckColumn && c.Visibility == Visibility.Visible).ToList();
        var manual = visible.Where(c => !double.IsNaN(UserWidth(c))).ToList();
        var auto = visible.Where(c => c != NameColumn && double.IsNaN(UserWidth(c))).ToList();

        foreach (var column in manual)
        {
            SetWidth(column, UserWidth(column));
        }

        var left = room - manual.Sum(UserWidth);
        var wanted = auto.Sum(c => _natural.GetValueOrDefault(c, 80));

        // имя растянуто руками — остаток делят только столбцы по содержимому
        var name = double.IsNaN(UserWidth(NameColumn)) ? NameColumn.MinWidth : 0;
        var share = wanted <= 0 || name + wanted <= left ? 1 : Math.Max(0, left - name) / wanted;

        foreach (var column in auto)
        {
            SetWidth(column, Math.Max(column.MinWidth, _natural.GetValueOrDefault(column, 80) * share));
        }

        if (double.IsNaN(UserWidth(NameColumn)))
        {
            SetWidth(NameColumn, Math.Max(NameColumn.MinWidth, left - auto.Sum(c => c.Width.DisplayValue)));
        }

        static void SetWidth(DataGridColumn column, double value)
        {
            if (Math.Abs(column.Width.DisplayValue - value) > 0.5 || !column.Width.IsAbsolute)
            {
                column.Width = new DataGridLength(Math.Floor(value));
            }
        }
    }

    /// <summary>Отпустили границу столбца в шапке — его ширина запоминается во вкладке.</summary>
    private void OnColumnResized(object sender, DragCompletedEventArgs e)
    {
        if (e.OriginalSource is not Thumb { TemplatedParent: DataGridColumnHeader { Column: { } column } } || column == CheckColumn)
        {
            return;
        }

        _state.Columns[HeaderOf(column)] = Math.Round(column.ActualWidth);
        StateChanged?.Invoke(this);
        FitColumns();
    }

    /// <summary>Правый щелчок по шапке — какие столбцы показывать.</summary>
    private ContextMenu ColumnsMenu()
    {
        var menu = new ContextMenu();

        foreach (var column in List.Columns)
        {
            var header = column.Header as string ?? string.Empty;

            if (header is "Имя" or "Где" || column == CheckColumn || (!IsRemote && header is "Права" or "Владелец" or "Куда ведёт"))
            {
                continue;
            }

            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = !IsHidden(header) };

            item.Click += (_, _) =>
            {
                if (item.IsChecked)
                {
                    _state.HiddenColumns.Remove(header);
                }
                else if (!_state.HiddenColumns.Contains(header))
                {
                    _state.HiddenColumns.Add(header);
                }

                ApplyColumns();
                StateChanged?.Invoke(this);
            };

            menu.Items.Add(item);
        }

        // растянутое руками — обратно по содержимому
        menu.Items.Add(new Separator());
        var reset = new MenuItem { Header = "Ширина столбцов — по содержимому", IsEnabled = _state.Columns.Count > 0 };
        reset.Click += (_, _) =>
        {
            _state.Columns.Clear();
            StateChanged?.Invoke(this);
            FitColumns();
        };
        menu.Items.Add(reset);

        return menu;
    }

    // ── адрес ────────────────────────────────────────────────────────────────

    private void ShowPath(string path)
    {
        if (_source is not { } source)
        {
            return;
        }

        PathBox.Text = source.Display(path);
        Crumbs.Children.Clear();

        var crumbs = source.Crumbs(path);

        for (var i = 0; i < crumbs.Count; i++)
        {
            if (Crumbs.Children.Count > 0 && !(source.IsRemote && i == 1))
            {
                var arrow = new TextBlock
                {
                    Text = "›",
                    Margin = new Thickness(1, 0, 1, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)FindResource("TextFillColorTertiaryBrush"),
                };

                // промах мимо кнопки на разделителе — не повод открывать ввод
                arrow.MouseLeftButtonDown += (_, e) => e.Handled = true;
                Crumbs.Children.Add(arrow);
            }

            var (title, target) = crumbs[i];
            var button = new Button
            {
                Content = new TextBlock { Text = title },
                Style = (Style)FindResource("CrumbButtonStyle"),
                ToolTip = source.Display(target),
            };

            button.Click += async (_, _) =>
            {
                if (target != _current || _searching)
                {
                    FolderMaskBox.Text = string.Empty;
                    await NavigateAsync(target);
                }
            };

            Crumbs.Children.Add(button);
        }

        // длинный путь не влезает — видна его конечная часть, как в проводнике
        CrumbScroll.ScrollToRightEnd();
    }

    private void OnCrumbBarClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        BeginPathEdit();
    }

    public void BeginPathEdit()
    {
        if (_source is null)
        {
            return;
        }

        PathBox.Text = _source.Display(_current);
        CrumbBar.Visibility = Visibility.Collapsed;
        PathBox.Visibility = Visibility.Visible;

        // фокус — после того как поле показалось, иначе он не встаёт
        Dispatcher.BeginInvoke(() =>
        {
            PathBox.Focus();
            PathBox.SelectAll();
            SuggestPopup.IsOpen = false;
        }, DispatcherPriority.Input);
    }

    private void EndPathEdit()
    {
        SuggestPopup.IsOpen = false;
        PathBox.Visibility = Visibility.Hidden;
        CrumbBar.Visibility = Visibility.Visible;
    }

    private void OnPathLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (PathBox.IsKeyboardFocusWithin || SuggestList.IsKeyboardFocusWithin)
        {
            return;
        }

        EndPathEdit();
    }

    private void OnPathPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!SuggestPopup.IsOpen || SuggestList.Items.Count == 0)
        {
            return;
        }

        if (e.Key == Key.Down)
        {
            e.Handled = true;
            SuggestList.SelectedIndex = Math.Min(SuggestList.SelectedIndex + 1, SuggestList.Items.Count - 1);
        }
        else if (e.Key == Key.Up)
        {
            e.Handled = true;
            SuggestList.SelectedIndex = Math.Max(SuggestList.SelectedIndex - 1, -1);
        }
        else if (e.Key == Key.Tab && SuggestList.SelectedItem is string pick)
        {
            e.Handled = true;
            PathBox.Text = pick;
            PathBox.CaretIndex = pick.Length;
        }
    }

    private async void OnPathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            var text = SuggestPopup.IsOpen && SuggestList.SelectedItem is string pick ? pick : PathBox.Text;
            EndPathEdit();
            FolderMaskBox.Text = string.Empty;
            await NavigateAsync(text);
            FocusList();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            EndPathEdit();
            FocusList();
        }
    }

    /// <summary>Подсказки: подпапки того, что уже вписано, — из прочитанного, без похода на сервер.</summary>
    private void OnPathTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_source is not { } source || PathBox.Visibility != Visibility.Visible || !PathBox.IsKeyboardFocusWithin)
        {
            return;
        }

        var text = PathBox.Text;
        var cut = Math.Max(text.LastIndexOf('/'), text.LastIndexOf('\\'));

        if (cut < 0)
        {
            SuggestPopup.IsOpen = false;
            return;
        }

        var dir = text[..(cut + 1)];
        var prefix = text[(cut + 1)..];
        var listed = source.Cached(source.Normalize(dir));

        if (listed is null)
        {
            _ = source.ListAsync(source.Normalize(dir), refresh: false).ContinueWith(
                _ => Dispatcher.BeginInvoke(() => OnPathTextChanged(sender, e)),
                TaskScheduler.Default);
            SuggestPopup.IsOpen = false;
            return;
        }

        var hits = listed
            .Where(i => i.IsDirectory && i.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .Select(i => i.FullPath)
            .ToList();

        SuggestList.ItemsSource = hits;
        SuggestList.SelectedIndex = -1;
        SuggestPopup.IsOpen = hits.Count > 0 && !(hits.Count == 1 && string.Equals(hits[0], text, StringComparison.OrdinalIgnoreCase));
    }

    private async void OnSuggestPicked(object sender, MouseButtonEventArgs e)
    {
        if (SuggestList.SelectedItem is string pick)
        {
            EndPathEdit();
            await NavigateAsync(pick);
            FocusList();
        }
    }

    // ── навигация ────────────────────────────────────────────────────────────

    private void UpdateNavButtons()
    {
        BackButton.IsEnabled = _back.Count > 0;
        ForwardButton.IsEnabled = _forward.Count > 0;
    }

    private async void OnBack(object sender, RoutedEventArgs e) => await GoBackAsync();

    private async void OnForward(object sender, RoutedEventArgs e) => await GoForwardAsync();

    private async void OnUp(object sender, RoutedEventArgs e) => await GoUpAsync();

    private async void OnHome(object sender, RoutedEventArgs e)
    {
        if (_source is { } source)
        {
            await NavigateAsync(source.HomePath);
        }
    }

    /// <summary>В корень: самая верхняя папка над открытой; из корня диска — «Этот компьютер».</summary>
    private async void OnRoot(object sender, RoutedEventArgs e)
    {
        if (_source is not { } source)
        {
            return;
        }

        var top = _current;

        while (source.Parent(top) is { Length: > 0 } up)
        {
            top = up;
        }

        if (top == _current && source.Parent(top) is { Length: 0 })
        {
            top = string.Empty;
        }

        await NavigateAsync(top);
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAllAsync();

    /// <summary>Корзина в шапке — то же, что Del.</summary>
    private async void OnDelete(object sender, RoutedEventArgs e) => await DeleteSelectedAsync();

    /// <summary>F5: папка с сервера заново и дерево — там могли появиться новые ветки.</summary>
    public async Task RefreshAllAsync()
    {
        if (_searching)
        {
            await SearchFoldersAsync();
            return;
        }

        ResetTree();
        _freeAt = default;
        await NavigateAsync(_current, record: false, refresh: true, focus: FocusedEntry?.Name);
    }

    private async Task GoBackAsync()
    {
        if (_back.Count == 0)
        {
            return;
        }

        var from = _current;

        if (await NavigateAsync(_back.Pop(), record: false) == NavResult.Ok)
        {
            _forward.Push(from);
            UpdateNavButtons();
        }
    }

    private async Task GoForwardAsync()
    {
        if (_forward.Count == 0)
        {
            return;
        }

        var from = _current;

        if (await NavigateAsync(_forward.Pop(), record: false) == NavResult.Ok)
        {
            _back.Push(from);
            UpdateNavButtons();
        }
    }

    private async Task GoUpAsync()
    {
        if (_searching)
        {
            FolderMaskBox.Text = string.Empty;
            return;
        }

        if (_source?.Parent(_current) is { } parent)
        {
            await NavigateAsync(parent);
        }
    }

    public Task OpenFolderAsync(string path, string? focus = null)
    {
        FolderMaskBox.Text = string.Empty;
        return NavigateAsync(path, focus: focus);
    }

    /// <summary>Enter или двойной щелчок: папка — открыть, файл — попросить страницу открыть его.</summary>
    private async Task OpenRowAsync(PaneRow row)
    {
        if (row.IsDirectory || row.IsParent)
        {
            // строки пересоздаются, и фокус уходит вместе со старой — без возврата следующий Enter терялся
            var hadFocus = IsKeyboardFocusWithin || List.IsMouseOver;
            FolderMaskBox.Text = string.Empty;
            await NavigateAsync(row.FullPath);

            if (hadFocus)
            {
                _ = Dispatcher.BeginInvoke(FocusList, DispatcherPriority.Loaded);
            }

            return;
        }

        CommandRequested?.Invoke(this, PaneCommand.Open);
    }

    private static DataGridRow? FindRow(DependencyObject? node)
    {
        while (node is not null and not DataGridRow)
        {
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return node as DataGridRow;
    }

    private void OnListFocused(object sender, KeyboardFocusChangedEventArgs e) => Activated?.Invoke(this);

    // ── клавиши ──────────────────────────────────────────────────────────────

    private async void OnPaneKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        // в полях ввода клавиши — их собственные
        if (e.OriginalSource is TextBox)
        {
            if (ctrl && key == Key.L)
            {
                e.Handled = true;
                BeginPathEdit();
            }

            return;
        }

        if ((ctrl && key == Key.L) || key == Key.F4)
        {
            e.Handled = true;
            BeginPathEdit();
        }
        else if (ctrl && key == Key.F)
        {
            e.Handled = true;
            FileMaskBox.Focus();
            FileMaskBox.SelectAll();
        }
        else if (alt && key == Key.Left)
        {
            e.Handled = true;
            await GoBackAsync();
        }
        else if (alt && key == Key.Right)
        {
            e.Handled = true;
            await GoForwardAsync();
        }
        else if ((alt && key == Key.Up) || (!alt && !ctrl && key == Key.Back))
        {
            e.Handled = true;
            await GoUpAsync();
        }
        else if (alt && key == Key.Enter)
        {
            e.Handled = true;
            CommandRequested?.Invoke(this, PaneCommand.Properties);
        }
        else if (key == Key.Enter && !ctrl && !shift)
        {
            // здесь, а не у таблицы: Enter работает, где бы в панели ни был фокус
            e.Handled = true;

            if (CursorRow is { IsRenaming: false } row)
            {
                await OpenRowAsync(row);
            }
        }
        else if (key == Key.F5 && TransferButton.Visibility == Visibility.Visible)
        {
            // как в Total Commander и WinSCP: F5 — копировать в соседнюю панель
            e.Handled = true;
            CommandRequested?.Invoke(this, PaneCommand.TransferToOther);
        }
        else if (key == Key.F5 || (ctrl && key == Key.R))
        {
            e.Handled = true;
            await RefreshAllAsync();
        }
        else if (key == Key.F2)
        {
            e.Handled = true;
            BeginRename();
        }
        else if (key == Key.F7 || (ctrl && shift && key == Key.N))
        {
            e.Handled = true;
            await CreateAsync(folder: true);
        }
        else if (key == Key.Delete)
        {
            e.Handled = true;
            await DeleteSelectedAsync();
        }
        else if (ctrl && key == Key.C)
        {
            e.Handled = true;
            CommandRequested?.Invoke(this, PaneCommand.Copy);
        }
        else if (ctrl && key == Key.X)
        {
            e.Handled = true;
            CommandRequested?.Invoke(this, PaneCommand.Cut);
        }
        else if (ctrl && key == Key.V)
        {
            e.Handled = true;
            CommandRequested?.Invoke(this, PaneCommand.Paste);
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox)
        {
            return;
        }

        // стрелки и листание двигают курсор, а не галочки; с Shift — отмечают диапазон, как обычно
        if (Keyboard.Modifiers == ModifierKeys.None && _view is { Count: > 0 } view
            && e.Key is Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
        {
            e.Handled = true;

            var index = CursorRow is { } at ? view.IndexOf(at) : -1;
            var page = Math.Max(1, (int)(List.ActualHeight / Math.Max(1, List.RowHeight is > 0 and var h ? h : 24)) - 2);

            index = e.Key switch
            {
                Key.Up => index - 1,
                Key.Down => index + 1,
                Key.Home => 0,
                Key.End => view.Count - 1,
                Key.PageUp => index - page,
                _ => index + page,
            };

            if (view.GetItemAt(Math.Clamp(index, 0, view.Count - 1)) is PaneRow target)
            {
                MoveCursor(target);
            }

            return;
        }

        // пробел — галочка у строки под курсором, и курсор дальше: отмечать подряд с клавиатуры
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None && CursorRow is { } spaced && _view is { } rows)
        {
            e.Handled = true;

            if (!spaced.IsParent && List.ItemContainerGenerator.ContainerFromItem(spaced) is DataGridRow container)
            {
                container.IsSelected = !container.IsSelected;
            }

            if (rows.IndexOf(spaced) + 1 < rows.Count && rows.GetItemAt(rows.IndexOf(spaced) + 1) is PaneRow next)
            {
                MoveCursor(next);
            }
        }
    }

    /// <summary>Набор букв — курсор на первое имя, которое с них начинается, как в проводнике.</summary>
    private void OnListTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.OriginalSource is TextBox || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0]) || _view is null)
        {
            return;
        }

        _typed = DateTime.Now - _typedAt > TimeSpan.FromSeconds(1) ? e.Text : _typed + e.Text;
        _typedAt = DateTime.Now;

        var rows = _view.Cast<PaneRow>().Where(r => !r.IsParent).ToList();
        var hit = rows.FirstOrDefault(r => r.Name.StartsWith(_typed, StringComparison.CurrentCultureIgnoreCase));

        if (hit is not null)
        {
            e.Handled = true;
            MoveCursor(hit);
        }
    }

    // ── создание, переименование, удаление ───────────────────────────────────

    private async void OnNewFolder(object sender, RoutedEventArgs e) => await CreateAsync(folder: true);

    private async void OnNewFile(object sender, RoutedEventArgs e) => await CreateAsync(folder: false);

    /// <summary>Новая папка или файл со свободным именем — и сразу правка имени, как в проводнике.</summary>
    public async Task CreateAsync(bool folder)
    {
        if (_source is not { } source || _current.Length == 0 && !source.IsRemote)
        {
            return;
        }

        EndSearch();

        var taken = _rows.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = Unique(folder ? "Новая папка" : "Новый текстовый документ.txt", taken);
        var path = source.Combine(_current, name);

        try
        {
            if (folder)
            {
                await source.CreateFolderAsync(path);
            }
            else
            {
                await source.CreateFileAsync(path);
            }

            Log($"{(folder ? "Создана папка" : "Создан файл")} {source.Display(path)}");
            await NavigateAsync(_current, record: false, refresh: true, focus: name);
            BeginRename();
        }
        catch (Exception ex)
        {
            Log($"Не удалось создать {source.Display(path)}: {ex.Message}", error: true);
        }
    }

    private static string Unique(string name, ISet<string> taken)
    {
        if (!taken.Contains(name))
        {
            return name;
        }

        var dot = name.LastIndexOf('.');
        var (stem, ext) = dot > 0 ? (name[..dot], name[dot..]) : (name, string.Empty);

        for (var n = 2; ; n++)
        {
            var candidate = $"{stem} ({n}){ext}";

            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>F2: имя правится прямо в строке; выделено имя без расширения, как в Windows.</summary>
    public void BeginRename()
    {
        if (CursorRow is not { IsParent: false } row || row.Entry.Kind != FsKind.Normal || _source is null)
        {
            return;
        }

        row.EditName = row.Name;
        row.IsRenaming = true;
        List.ScrollIntoView(row);

        Dispatcher.BeginInvoke(() =>
        {
            if (List.ItemContainerGenerator.ContainerFromItem(row) is not DataGridRow container
                || FindChild<TextBox>(container, "RenameBox") is not { } box)
            {
                return;
            }

            box.Focus();
            var dot = row.IsDirectory ? -1 : row.Name.LastIndexOf('.');
            box.Select(0, dot > 0 ? dot : row.Name.Length);
        }, DispatcherPriority.Loaded);
    }

    private async void OnRenameKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: PaneRow row })
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await CommitRenameAsync(row);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            row.IsRenaming = false;
            FocusList();
        }
    }

    private async void OnRenameLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: PaneRow { IsRenaming: true } row })
        {
            await CommitRenameAsync(row);
        }
    }

    private bool _committing;

    private async Task CommitRenameAsync(PaneRow row)
    {
        if (_committing || _source is not { } source)
        {
            return;
        }

        var name = row.EditName.Trim();

        if (name.Length == 0 || name == row.Name)
        {
            row.IsRenaming = false;
            FocusList();
            return;
        }

        var bad = source.IsRemote ? name.Contains('/') || name is "." or ".." : name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0;

        if (bad)
        {
            Log($"Недопустимое имя: {name}", error: true);
            return;
        }

        var parent = source.Parent(row.FullPath) ?? _current;
        var target = source.Combine(parent, name);

        _committing = true;

        try
        {
            await source.RenameAsync(row.FullPath, target);
            row.IsRenaming = false;
            Log($"Переименовано: {row.Name} → {name}");
            await NavigateAsync(_current, record: false, refresh: true, focus: name);
            FocusList();
        }
        catch (Exception ex)
        {
            // занятое имя — ошибка, а поле остаётся открытым: можно поправить и нажать Enter ещё раз
            Log($"Не переименовано {row.Name}: {ex.Message}", error: true);
        }
        finally
        {
            _committing = false;
        }
    }

    public async Task DeleteSelectedAsync()
    {
        if (_source is not { } source || TargetEntries is not { Count: > 0 } items)
        {
            return;
        }

        var dirs = items.Count(i => i.IsDirectory && !i.IsLink);
        var files = items.Count - dirs;
        var what = string.Join(", ", new[]
        {
            dirs > 0 ? $"{dirs} {Plural(dirs, "папку", "папки", "папок")}" : null,
            files > 0 ? $"{files} {Plural(files, "файл", "файла", "файлов")}" : null,
        }.Where(s => s is not null));

        var names = string.Join("\n", items.Take(10).Select(i => i.Name))
            + (items.Count > 10 ? $"\nи ещё {items.Count - 10}" : string.Empty);

        var ok = ConfirmDialog.Ask(
            Window.GetWindow(this),
            source.IsRemote ? $"Удалить безвозвратно {what}?" : $"Отправить в корзину {what}?",
            source.IsRemote
                ? $"На сервере корзины нет — восстановить будет нельзя. Папки удаляются со всем содержимым.\n\n{names}"
                : names,
            source.IsRemote ? "Удалить" : "В корзину");

        if (!ok)
        {
            return;
        }

        var index = List.SelectedIndex;

        try
        {
            StatusText.Text = "удаление…";
            var problems = await source.DeleteAsync(items);

            foreach (var line in problems)
            {
                Log(line, error: true);
            }

            if (problems.Count == 0)
            {
                Log($"Удалено: {what} в {source.Display(_current)}");
            }

            await NavigateAsync(_current, record: false, refresh: true);

            // курсор — на соседнюю строку, как после удаления в проводнике
            if (_view is not null && _view.Count > 0)
            {
                List.SelectedIndex = Math.Clamp(index, 0, _view.Count - 1);
            }

            FocusList();
        }
        catch (Exception ex)
        {
            Log("Удаление не удалось: " + ex.Message, error: true);
        }
    }

    private static string Plural(int n, string one, string few, string many) =>
        (n % 10, n % 100) switch
        {
            (1, not 11) => one,
            (>= 2 and <= 4, < 12 or > 14) => few,
            _ => many,
        };

    private void Log(string text, bool error = false)
    {
        var where = _source is RemoteSource remote ? remote.Server.Display : LocalSource.ComputerTitle;

        if (error)
        {
            AppLog.Error(text, where, AppSection.Explorer);
        }
        else
        {
            AppLog.Success(text, where, AppSection.Explorer);
        }
    }

    // ── контекстное меню ─────────────────────────────────────────────────────

    private void OnListRightDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;

        // по шапке — какие столбцы показывать
        if (FindParent<DataGridColumnHeader>(source) is not null)
        {
            e.Handled = true;
            var columns = ColumnsMenu();
            columns.PlacementTarget = List;
            columns.IsOpen = true;
            return;
        }

        // Галочки правый щелчок не трогает. По отмеченной строке меню — про все отмеченные,
        // по неотмеченной — про неё одну (курсор встаёт на неё), по пустому месту — про папку.
        var clicked = FindRow(source)?.Item as PaneRow;

        if (clicked is not null)
        {
            MoveCursor(clicked);
        }
        else
        {
            FocusList();
        }

        _menuTargets = clicked is null || clicked.IsParent || clicked.Entry.Kind != FsKind.Normal
            ? []
            : List.SelectedItems.Contains(clicked) ? SelectedEntries : [clicked.Entry];

        e.Handled = true;
        var menu = BuildMenu();
        menu.PlacementTarget = List;

        // действие из меню уже прочитало, с чем работать, — после закрытия снова галочки и курсор
        menu.Closed += (_, _) => Dispatcher.BeginInvoke(() => _menuTargets = null, DispatcherPriority.Background);
        menu.IsOpen = true;
    }

    /// <summary>
    /// Меню правой кнопки: у каждого пункта значок, как в меню Windows, — пункт находится
    /// глазами по значку, а не чтением всего столбца. Подписи короткие, клавиши справа.
    /// </summary>
    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        var picked = TargetEntries;
        var single = picked.Count == 1 ? picked[0] : null;
        var remote = IsRemote;
        var canCreate = _current.Length > 0 || remote;

        void Add(Wpf.Ui.Controls.SymbolRegular icon, string header, string gesture, Action action, bool enabled = true)
        {
            var item = new MenuItem
            {
                Header = new TextBlock { Text = header },
                Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = icon, FontSize = 16 },
                InputGestureText = gesture,
                IsEnabled = enabled,
            };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        void Line() => menu.Items.Add(new Separator());

        void Ask(PaneCommand command) => CommandRequested?.Invoke(this, command);

        var editable = picked.Where(p => !p.IsDirectory && TextFile.IsEditable(p.Name)).ToList();

        // открыть
        if (single is not null)
        {
            Add(single.IsDirectory ? Wpf.Ui.Controls.SymbolRegular.FolderOpen24 : Wpf.Ui.Controls.SymbolRegular.Open24,
                "Открыть", "Enter", () => _ = OpenRowAsync(new PaneRow(single, remote)));

            if (!single.IsDirectory)
            {
                Add(Wpf.Ui.Controls.SymbolRegular.AppsList24, "Открыть с помощью…", string.Empty, () => Ask(PaneCommand.OpenWith));
            }
        }

        if (editable.Count > 0)
        {
            Add(Wpf.Ui.Controls.SymbolRegular.DocumentEdit24, editable.Count == 1 ? "Править" : $"Править ({editable.Count})",
                string.Empty, () => EditFiles(editable));
        }

        if (remote && single is { IsLink: true } link && link.LinkTarget.Length > 0)
        {
            Add(Wpf.Ui.Controls.SymbolRegular.ArrowForward24, "Перейти к цели ссылки", string.Empty, () => _ = GoToLinkTargetAsync(link));
        }

        if (single is not null || editable.Count > 0)
        {
            Line();
        }

        // копирование и буфер
        if (picked.Count > 0)
        {
            Add(remote ? Wpf.Ui.Controls.SymbolRegular.ArrowDownload24 : Wpf.Ui.Controls.SymbolRegular.ArrowUpload24,
                remote ? "Скачать на компьютер" : "Отправить на сервер",
                TransferButton.Visibility == Visibility.Visible ? "F5" : string.Empty,
                () => Ask(PaneCommand.TransferToOther));
            Add(Wpf.Ui.Controls.SymbolRegular.Copy24, "Копировать", "Ctrl+C", () => Ask(PaneCommand.Copy));
            Add(Wpf.Ui.Controls.SymbolRegular.Cut24, "Вырезать", "Ctrl+X", () => Ask(PaneCommand.Cut));
        }

        Add(Wpf.Ui.Controls.SymbolRegular.ClipboardPaste24, "Вставить", "Ctrl+V", () => Ask(PaneCommand.Paste), canCreate);

        // правка самих файлов
        if (picked.Count > 0)
        {
            Line();
            Add(Wpf.Ui.Controls.SymbolRegular.Rename24, "Переименовать", "F2", BeginRename, single is not null);
            Add(Wpf.Ui.Controls.SymbolRegular.Delete24, remote ? "Удалить" : "Удалить в корзину", "Del", () => _ = DeleteSelectedAsync());
        }

        Line();
        Add(Wpf.Ui.Controls.SymbolRegular.FolderAdd24, "Новая папка", "F7", () => _ = CreateAsync(folder: true), canCreate);
        Add(Wpf.Ui.Controls.SymbolRegular.DocumentAdd24, "Новый файл", string.Empty, () => _ = CreateAsync(folder: false), canCreate);

        // путь и сравнение
        Line();
        Add(Wpf.Ui.Controls.SymbolRegular.Link24, "Скопировать путь", string.Empty, CopyPath);

        if (single is { IsDirectory: false })
        {
            Add(Wpf.Ui.Controls.SymbolRegular.BranchCompare24, "Сравнить с соседней панелью", string.Empty, () => Ask(PaneCommand.Compare));
        }

        // сервер и другие разделы
        if (remote)
        {
            Line();

            if (single is { IsDirectory: false, IsExecutable: true })
            {
                Add(Wpf.Ui.Controls.SymbolRegular.Play24, "Выполнить на сервере…", string.Empty, () => Ask(PaneCommand.Run));
            }

            Add(Wpf.Ui.Controls.SymbolRegular.WindowConsole20, "Команда в этой папке", string.Empty, () => Ask(PaneCommand.CommandTabHere));
            Add(Wpf.Ui.Controls.SymbolRegular.FolderArrowRight24, "Папка назначения в «Копировании»", string.Empty, () => Ask(PaneCommand.SendToCopy));

            if (picked.Count > 0)
            {
                Add(Wpf.Ui.Controls.SymbolRegular.ArrowDownload24, "Добавить в «Скачивание»", string.Empty, () => Ask(PaneCommand.SendToDownload));
            }
        }

        Line();
        Add(Wpf.Ui.Controls.SymbolRegular.Info24, "Свойства", "Alt+Enter", () => Ask(PaneCommand.Properties));

        return menu;
    }

    private void OnEdit(object sender, RoutedEventArgs e) => EditFiles(TargetEntries);

    /// <summary>«Править»: каждый файл — своим окном встроенного редактора.</summary>
    private void EditFiles(IReadOnlyList<FsEntry> entries)
    {
        if (_source is not { } source)
        {
            return;
        }

        var files = entries.Where(e => !e.IsDirectory && e.Kind == FsKind.Normal).Take(10).ToList();

        if (files.Count == 0)
        {
            AppLog.Hint("Поставьте курсор на файл: править можно только файл.");
            return;
        }

        foreach (var file in files)
        {
            CodeEditorWindow.Open(Window.GetWindow(this), source, file.FullPath, file.Name);
        }
    }

    private void CopyPath()
    {
        var paths = TargetEntries.Select(e => e.FullPath).ToList();

        if (paths.Count == 0 && _source is { } source)
        {
            paths.Add(source.Display(_current));
        }

        ClipboardText.Copy(string.Join(Environment.NewLine, paths));
        AppLog.Hint(paths.Count == 1 ? $"Путь скопирован: {paths[0]}" : $"Путей скопировано: {paths.Count}");
    }

    private async Task GoToLinkTargetAsync(FsEntry link)
    {
        if (_source is not { } source)
        {
            return;
        }

        // относительная ссылка — от её папки
        var target = link.LinkTarget.StartsWith('/')
            ? link.LinkTarget
            : source.Combine(source.Parent(link.FullPath) ?? _current, link.LinkTarget);

        if (link.IsDirectory)
        {
            await OpenFolderAsync(target);
        }
        else if (source.Parent(source.Normalize(target)) is { } parent)
        {
            await OpenFolderAsync(parent, Path.GetFileName(target));
        }
    }

    // ── мышь: рамка выделения, перетаскивание файлов, приём из проводника Windows ──

    /// <summary>
    /// Протяжка левой кнопкой за имя — перетаскивание файлов, как в проводнике; за другие
    /// столбцы и с пустого места под строками — рамка выделения. Галочка добавляет или снимает
    /// свою строку.
    /// </summary>
    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        Activated?.Invoke(this);
        _marqueeFrom = null;
        _dragFrom = null;

        var hit = e.OriginalSource as DependencyObject;

        if (FindParent<DataGridColumnHeader>(hit) is not null || FindParent<ScrollBar>(hit) is not null || FindParent<TextBox>(hit) is not null)
        {
            return;
        }

        var row = FindRow(hit);

        if (row is { Item: PaneRow { IsParent: false } } && FindParent<CheckBox>(hit) is not null)
        {
            row.IsSelected = !row.IsSelected;
            e.Handled = true;
            FocusList();
            return;
        }

        if (_rows.Any(r => r.IsRenaming))
        {
            return;
        }

        // двойной щелчок — здесь же: щелчок ниже гасится, и до события двойного щелчка таблица не доходит
        if (e.ClickCount == 2 && row is { Item: PaneRow opened })
        {
            e.Handled = true;
            _ = OpenRowAsync(opened);
            return;
        }

        // щелчок по строке — только курсор, галочку ставит сам чекбокс (или Ctrl/Shift со щелчком)
        var plain = Keyboard.Modifiers == ModifierKeys.None;

        if (plain && row is { Item: PaneRow clicked })
        {
            MoveCursor(clicked);
            e.Handled = true;
        }
        else if (row is null)
        {
            // щелчок по пустому месту под строками — клавиши тоже сюда
            FocusList();
        }

        if (e.ClickCount > 1)
        {
            return;
        }

        var from = e.GetPosition(List);

        if (plain && row is { Item: PaneRow { IsParent: false, Entry.Kind: FsKind.Normal } grabbed }
            && FindParent<DataGridCell>(hit)?.Column == NameColumn)
        {
            _dragFrom = from;
            _dragRow = grabbed;
            return;
        }

        var index = row is null ? List.Items.Count : List.ItemContainerGenerator.IndexFromContainer(row);

        _marqueeFrom = from;
        _marqueeIndex = index;
        _marqueeOnEmpty = row is null;
        _marqueeBase = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? List.SelectedItems.Cast<object>().ToList() : [];

        // рамка держится за строку, а не за точку на экране: список при протяжке прокручивается
        var anchor = row ?? LastRow();
        _marqueeRow = anchor is null ? -1 : List.ItemContainerGenerator.IndexFromContainer(anchor);
        _marqueeOffset = anchor is null ? 0 : from.Y - anchor.TranslatePoint(default, List).Y;
    }

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragFrom is { } grab)
        {
            var moved = e.GetPosition(List);

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _dragFrom = null;
            }
            else if (Math.Abs(moved.X - grab.X) >= SystemParameters.MinimumHorizontalDragDistance
                || Math.Abs(moved.Y - grab.Y) >= SystemParameters.MinimumVerticalDragDistance)
            {
                _dragFrom = null;
                e.Handled = true;
                StartDrag(_dragRow!);
            }

            return;
        }

        if (_marqueeFrom is not { } origin || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var at = e.GetPosition(List);

        if (!_marqueeOn)
        {
            if (Math.Abs(at.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(at.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            BeginMarquee();
        }

        UpdateMarquee(at);
        e.Handled = true;
    }

    private string? DropFolder(DragEventArgs e)
    {
        var row = FindRow(e.OriginalSource as DependencyObject)?.Item as PaneRow;

        if (row is { IsDirectory: true, IsParent: false } && row.Entry.Kind != FsKind.Drive || row is { Entry.Kind: FsKind.Place })
        {
            return row.FullPath;
        }

        return _current.Length > 0 || IsRemote ? _current : null;
    }

    /// <summary>Свои строки в перетаскивании: откуда и что. Рядом — пути для проводника Windows, если это файлы компьютера.</summary>
    private sealed record PaneDrag(FilePane From, IReadOnlyList<FsEntry> Items);

    private const string PaneDragFormat = "MultiServerSync.PaneDrag";

    /// <summary>Нажали на имя строки — с этой точки пойдёт перетаскивание, а не рамка.</summary>
    private Point? _dragFrom;
    private PaneRow? _dragRow;

    /// <summary>Куда упадёт — пишет панель под мышью, показывает подпись у курсора.</summary>
    private static string? _dropHint;
    private static long _dropHintAt;

    /// <summary>
    /// Перетаскивание мышью — то же копирование, что F5 и кнопка: с галочками — отмеченное,
    /// без них — строка, за которую взяли. В соседнюю панель, в папку этой или в проводник Windows.
    /// </summary>
    private void StartDrag(PaneRow grabbed)
    {
        var items = SelectedEntries is { Count: > 0 } picked ? picked : [grabbed.Entry];
        var first = items.Count == 1 ? grabbed : List.SelectedItems.OfType<PaneRow>().FirstOrDefault() ?? grabbed;
        var title = items.Count == 1 ? items[0].Name : $"{items.Count} {Plural(items.Count, "объект", "объекта", "объектов")}";

        var data = new DataObject();
        data.SetData(PaneDragFormat, new PaneDrag(this, items));

        // файлы компьютера можно бросить и в проводник Windows
        if (!IsRemote)
        {
            var paths = new System.Collections.Specialized.StringCollection();
            paths.AddRange(items.Select(i => i.FullPath).ToArray());
            data.SetFileDropList(paths);
        }

        var ghost = new DragGhost(this, first.Icon, title, items.Count);

        void OnFeedback(object sender, GiveFeedbackEventArgs e)
        {
            ghost.Follow();
            var fresh = Environment.TickCount64 - _dropHintAt < 400;
            ghost.SetAction(e.Effects == DragDropEffects.None ? null : fresh ? _dropHint : "Копировать");
        }

        List.GiveFeedback += OnFeedback;

        try
        {
            DragDrop.DoDragDrop(List, data, DragDropEffects.Copy);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // принимающая программа оборвала перетаскивание — ничего не делаем
        }
        finally
        {
            List.GiveFeedback -= OnFeedback;
            ghost.Close();
            _dropHint = null;
        }
    }

    /// <summary>Можно ли бросить свои строки в эту папку: не в ту же, где они лежат, и не в саму себя.</summary>
    private bool CanDrop(PaneDrag drag, string target)
    {
        if (DropRequested is null || drag.From._source is not { } from || _source is not { } to)
        {
            return false;
        }

        var sameSide = ReferenceEquals(from, to) || from is RemoteSource a && to is RemoteSource b && a.Server == b.Server;

        if (!sameSide)
        {
            return true;
        }

        var comparison = to.IsRemote ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var separator = to.IsRemote ? "/" : "\\";
        var into = to.Normalize(target);

        return drag.Items.All(item =>
        {
            var path = to.Normalize(item.FullPath);
            return !string.Equals(to.Parent(path), into, comparison)
                && !string.Equals(path, into, comparison)
                && !into.StartsWith(path.TrimEnd('/', '\\') + separator, comparison);
        });
    }

    /// <summary>Свои строки — копированием, как F5; файлы из проводника Windows — на сервер заливкой, сюда — копией.</summary>
    private void OnListDragOver(object sender, DragEventArgs e)
    {
        var target = DropFolder(e);
        e.Handled = true;

        if (e.Data.GetData(PaneDragFormat) is PaneDrag drag)
        {
            var ok = target is not null && CanDrop(drag, target);
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            MarkDropRow(ok ? e : null);
            _dropHint = ok ? $"Копировать в {_source!.Display(target!)}" : null;
            _dropHintAt = Environment.TickCount64;
            return;
        }

        e.Effects = target is null || _source is null || !e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.None
            : (e.KeyStates & DragDropKeyStates.ShiftKey) != 0 && !IsRemote ? DragDropEffects.Move : DragDropEffects.Copy;
        MarkDropRow(e.Effects == DragDropEffects.None ? null : e);
    }

    private void OnListDragLeave(object sender, DragEventArgs e) => MarkDropRow(null);

    private void OnListDrop(object sender, DragEventArgs e)
    {
        MarkDropRow(null);

        if (DropFolder(e) is not { } target || _source is null)
        {
            return;
        }

        e.Handled = true;

        if (e.Data.GetData(PaneDragFormat) is PaneDrag drag)
        {
            if (CanDrop(drag, target))
            {
                DropRequested?.Invoke(this, new PaneDrop(drag.From, drag.Items, [], target, Move: false));
            }

            return;
        }

        var move = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0;

        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            DropRequested?.Invoke(this, new PaneDrop(null, [], files, target, move));
        }
    }

    private PaneRow? _dropRow;

    /// <summary>Папка под мышью, в которую упадёт перетаскиваемое, — подсвечена. Null — подсветку снять.</summary>
    private void MarkDropRow(DragEventArgs? e)
    {
        var row = e is null ? null : FindRow(e.OriginalSource as DependencyObject)?.Item as PaneRow;

        if (row is not null && DropFolder(e!) != row.FullPath)
        {
            row = null;
        }

        if (ReferenceEquals(row, _dropRow))
        {
            return;
        }

        if (_dropRow is not null)
        {
            _dropRow.IsDropTarget = false;
        }

        _dropRow = row;

        if (row is not null)
        {
            row.IsDropTarget = true;
        }
    }

    // ── дерево ───────────────────────────────────────────────────────────────

    private void OnToggleTree(object sender, RoutedEventArgs e) => ShowTree(!_state.ShowTree, save: true);

    private void ShowTree(bool show, bool save)
    {
        TreeHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        TreeSplitter.Visibility = TreeHost.Visibility;
        TreeColumn.Width = show ? new GridLength(200) : new GridLength(0);
        TreeSplitColumn.Width = show ? new GridLength(4) : new GridLength(0);
        TreeButton.Appearance = show ? Wpf.Ui.Controls.ControlAppearance.Secondary : Wpf.Ui.Controls.ControlAppearance.Transparent;

        if (save && _state.ShowTree != show)
        {
            _state.ShowTree = show;
            StateChanged?.Invoke(this);

            if (show)
            {
                ResetTree();
                _ = RevealInTreeAsync(_current, _navVersion);
            }
        }
    }

    private void ResetTree()
    {
        _treeSelected = null;

        if (_source is not { } source)
        {
            _treeRoot = null;
            Tree.ItemsSource = null;
            return;
        }

        _treeRoot = source.IsRemote
            ? new PaneNode("/", "/", ShellIcons.Server)
            : new PaneNode(LocalSource.ComputerTitle, string.Empty, ShellIcons.Computer);
        Tree.ItemsSource = new ObservableCollection<PaneNode> { _treeRoot };
    }

    private async Task LoadChildrenAsync(PaneNode node)
    {
        if (node.IsLoaded || node.IsPlaceholder || _source is not { } source)
        {
            return;
        }

        node.IsLoaded = true;
        IReadOnlyList<FsEntry> items;

        try
        {
            items = source.Cached(node.FullPath) ?? await source.ListAsync(node.FullPath, refresh: false);
        }
        catch
        {
            // нет прав или папка пропала — ветка просто пустая
            items = [];
        }

        node.Children.Clear();

        foreach (var dir in items.Where(c => c.IsDirectory).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            var child = new PaneNode(dir.Name, dir.FullPath,
                source.IsRemote ? ShellIcons.Folder : ShellIcons.ForLocal(dir.FullPath, true, dir.Kind == FsKind.Drive));

            // стрелка — только у папки, где точно есть подпапки: ложная стрелка обещает то, чего нет
            if (source is RemoteSource remote && remote.HasSubdirs(dir.FullPath) == false)
            {
                child.IsLoaded = true;
                child.Children.Clear();
            }

            node.Children.Add(child);
        }
    }

    private async void OnNodeExpanded(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TreeViewItem { DataContext: PaneNode node })
        {
            await LoadChildrenAsync(node);
        }
    }

    private async void OnTreeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not PaneNode { IsPlaceholder: false } node)
        {
            return;
        }

        _treeSelected = node;

        // Переходит только выбор человеком — щелчок или клавиши в самом дереве. Подключение
        // вкладки пересоздаёт дерево, и оно само «выбирало» корень: панель уходила в «Этот
        // компьютер» (на сервере — в «/») и затирала сохранённую папку вкладки.
        if (_syncingTree || node.FullPath == _current || !Tree.IsKeyboardFocusWithin)
        {
            return;
        }

        await OpenFolderAsync(node.FullPath);
    }

    /// <summary>Дерево разворачивается до открытой папки — как в проводнике.</summary>
    private async Task RevealInTreeAsync(string path, int version)
    {
        if (!_state.ShowTree || _treeRoot is not { } node || _source is not { } source)
        {
            return;
        }

        var chain = source.Crumbs(path).Skip(1).Select(c => c.Path).ToList();

        // у компьютера первая «крошка» — диск, его путь с «\» на конце
        try
        {
            _syncingTree = true;

            foreach (var step in chain)
            {
                await LoadChildrenAsync(node);

                if (version != _navVersion)
                {
                    return;
                }

                node.IsExpanded = true;
                var next = node.Children.FirstOrDefault(c => string.Equals(
                    c.FullPath.TrimEnd('\\', '/'), step.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));

                if (next is null)
                {
                    break;
                }

                node = next;
            }

            if (_treeSelected is { } previous && !ReferenceEquals(previous, node))
            {
                previous.IsSelected = false;
            }

            node.IsSelected = true;
            _treeSelected = node;
        }
        finally
        {
            _syncingTree = false;
        }
    }

    // ── закладки и последние папки ───────────────────────────────────────────

    private List<string> Bookmarks
    {
        get
        {
            var settings = AppState.Current.Settings;

            if (_source is RemoteSource remote)
            {
                if (!settings.ExplorerRemoteBookmarks.TryGetValue(remote.Server.Id, out var list))
                {
                    list = [];
                    settings.ExplorerRemoteBookmarks[remote.Server.Id] = list;
                }

                return list;
            }

            return settings.ExplorerLocalBookmarks;
        }
    }

    private List<string> Recent
    {
        get
        {
            var settings = AppState.Current.Settings;

            if (_source is RemoteSource remote)
            {
                if (!settings.ExplorerRemoteRecent.TryGetValue(remote.Server.Id, out var list))
                {
                    list = [];
                    settings.ExplorerRemoteRecent[remote.Server.Id] = list;
                }

                return list;
            }

            return settings.ExplorerLocalRecent;
        }
    }

    private void PushRecent(string path)
    {
        if (path.Length == 0 && _source is { IsRemote: false })
        {
            return;
        }

        var recent = Recent;
        recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        recent.Insert(0, path);

        if (recent.Count > 10)
        {
            recent.RemoveRange(10, recent.Count - 10);
        }
    }

    /// <summary>★: закладки этого места, ниже — последние папки. Крестик в строке убирает закладку.</summary>
    private void OnBookmarks(object sender, RoutedEventArgs e)
    {
        if (_source is not { } source)
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = BookmarkButton, Placement = PlacementMode.Bottom };
        var bookmarks = Bookmarks;
        var here = _current;
        var marked = bookmarks.Any(b => string.Equals(b, here, StringComparison.OrdinalIgnoreCase));

        if (here.Length > 0 || source.IsRemote)
        {
            var toggle = new MenuItem
            {
                Header = new TextBlock { Text = marked ? "Убрать эту папку из закладок" : "Добавить эту папку в закладки" },
                Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = marked ? Wpf.Ui.Controls.SymbolRegular.StarOff24 : Wpf.Ui.Controls.SymbolRegular.StarAdd24 },
            };

            toggle.Click += (_, _) =>
            {
                if (marked)
                {
                    bookmarks.RemoveAll(b => string.Equals(b, here, StringComparison.OrdinalIgnoreCase));
                    AppLog.Hint($"Закладка убрана: {source.Display(here)}");
                }
                else
                {
                    bookmarks.Add(here);
                    AppLog.Hint($"Закладка добавлена: {source.Display(here)}");
                }

                AppState.Current.SaveSettings();
            };

            menu.Items.Add(toggle);
        }

        if (bookmarks.Count > 0)
        {
            menu.Items.Add(new Separator());

            foreach (var path in bookmarks.ToList())
            {
                menu.Items.Add(PathItem(path, removable: true));
            }
        }

        var recent = Recent.Where(p => !bookmarks.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();

        if (recent.Count > 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = new TextBlock { Text = "Последние" }, IsEnabled = false });

            foreach (var path in recent)
            {
                menu.Items.Add(PathItem(path, removable: false));
            }
        }

        menu.IsOpen = true;

        MenuItem PathItem(string path, bool removable)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = source.Display(path), VerticalAlignment = VerticalAlignment.Center });

            var item = new MenuItem { Header = row };

            if (removable)
            {
                var remove = new Button { Style = (Style)FindResource("HistoryRemoveButtonStyle"), ToolTip = "Убрать из закладок" };
                Grid.SetColumn(remove, 1);
                remove.Click += (_, args) =>
                {
                    args.Handled = true;
                    bookmarks.RemoveAll(b => string.Equals(b, path, StringComparison.OrdinalIgnoreCase));
                    AppState.Current.SaveSettings();
                    menu.Items.Remove(item);
                    AppLog.Hint($"Закладка убрана: {source.Display(path)}");
                };
                row.Children.Add(remove);
            }

            item.Click += async (_, _) => await OpenFolderAsync(path);
            return item;
        }
    }

    // ── рамка выделения ──────────────────────────────────────────────────────

    /// <summary>Где нажали, пока рамка ещё не пошла; пусто — нажатие не для рамки.</summary>
    private Point? _marqueeFrom;

    /// <summary>Строка, с которой начали, — или число строк, если начали под ними.</summary>
    private int _marqueeIndex;

    /// <summary>Строка, за которую держится угол рамки, и сдвиг точки от её верха.</summary>
    private int _marqueeRow;
    private double _marqueeOffset;

    private bool _marqueeOn;
    private bool _marqueeOnEmpty;
    private bool _marqueeCapturing;

    /// <summary>Что было выделено до рамки с Ctrl: рамка добавляет к нему, а не заменяет.</summary>
    private List<object> _marqueeBase = [];

    private DispatcherTimer? _marqueeScroll;

    private void BeginMarquee()
    {
        _marqueeOn = true;

        // у таблицы своя протяжка выделения — снимаем её захват мыши и берём свой
        _marqueeCapturing = true;
        List.ReleaseMouseCapture();
        List.CaptureMouse();
        _marqueeCapturing = false;

        Marquee.Visibility = Visibility.Visible;

        _marqueeScroll ??= new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Input, (_, _) => ScrollMarquee(), Dispatcher);
        _marqueeScroll.Start();
    }

    private void EndMarquee()
    {
        _marqueeOn = false;
        _marqueeFrom = null;
        _marqueeScroll?.Stop();
        Marquee.Visibility = Visibility.Collapsed;

        if (List.IsMouseCaptured)
        {
            _marqueeCapturing = true;
            List.ReleaseMouseCapture();
            _marqueeCapturing = false;
        }
    }

    private void OnListMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragFrom = null;

        if (_marqueeOn)
        {
            EndMarquee();
            return;
        }

        // щелчок по пустому месту под строками снимает выделение, как в проводнике
        if (_marqueeFrom is not null && _marqueeOnEmpty && Keyboard.Modifiers == ModifierKeys.None)
        {
            List.UnselectAll();
        }

        _marqueeFrom = null;
    }

    private void OnListLostCapture(object sender, MouseEventArgs e)
    {
        if (_marqueeOn && !_marqueeCapturing)
        {
            EndMarquee();
        }
    }

    /// <summary>Мышь за краем списка — список едет сам, рамка тянется за ним.</summary>
    private void ScrollMarquee()
    {
        if (!_marqueeOn || Mouse.LeftButton != MouseButtonState.Pressed || FindChild<ScrollViewer>(List) is not { } scroll)
        {
            return;
        }

        var at = Mouse.GetPosition(List);
        var (top, bottom) = RowsArea();

        if (at.Y < top)
        {
            scroll.LineUp();
        }
        else if (at.Y > bottom)
        {
            scroll.LineDown();
        }
        else
        {
            return;
        }

        List.UpdateLayout();
        UpdateMarquee(at);
    }

    private void UpdateMarquee(Point at)
    {
        var (top, bottom) = RowsArea();
        var y = Math.Clamp(at.Y, top, Math.Max(top, bottom - 1));
        var x = Math.Clamp(at.X, 0, List.ActualWidth);

        // угол, откуда тянули: строка уехала за край — угол прижат к краю
        var anchorY = (_marqueeRow >= 0 ? List.ItemContainerGenerator.ContainerFromIndex(_marqueeRow) as DataGridRow : null) is { } anchor
            ? anchor.TranslatePoint(default, List).Y + _marqueeOffset
            : _marqueeRow < FirstVisibleIndex() ? top : bottom;
        anchorY = Math.Clamp(anchorY, top, bottom);

        var fromX = Math.Clamp(_marqueeFrom?.X ?? x, 0, List.ActualWidth);
        Canvas.SetLeft(Marquee, Math.Min(fromX, x));
        Canvas.SetTop(Marquee, Math.Min(anchorY, y));
        Marquee.Width = Math.Abs(x - fromX);
        Marquee.Height = Math.Abs(y - anchorY);

        // строки между началом и концом рамки; «..» не выделяется
        var current = IndexAt(y);
        var first = Math.Min(_marqueeIndex, current);
        var last = Math.Min(Math.Max(_marqueeIndex, current), List.Items.Count - 1);
        var wanted = new HashSet<object>(_marqueeBase);

        for (var i = first; i <= last; i++)
        {
            if (List.Items[i] is PaneRow { IsParent: false } item)
            {
                wanted.Add(item);
            }
        }

        var selected = new HashSet<object>(List.SelectedItems.Cast<object>());

        foreach (var stale in selected.Where(o => !wanted.Contains(o)).ToList())
        {
            List.SelectedItems.Remove(stale);
        }

        foreach (var item in wanted.Where(o => !selected.Contains(o)).ToList())
        {
            List.SelectedItems.Add(item);
        }
    }

    /// <summary>Строка на этой высоте; под последней строкой — число строк.</summary>
    private int IndexAt(double y)
    {
        var hit = List.InputHitTest(new Point(Math.Max(1, NameColumn.ActualWidth / 2), y)) as DependencyObject;
        return FindRow(hit) is { } row ? List.ItemContainerGenerator.IndexFromContainer(row) : List.Items.Count;
    }

    private int FirstVisibleIndex()
    {
        var (top, _) = RowsArea();
        var index = IndexAt(top + 1);
        return index == List.Items.Count ? 0 : index;
    }

    private DataGridRow? LastRow() =>
        List.Items.Count == 0 ? null : List.ItemContainerGenerator.ContainerFromIndex(List.Items.Count - 1) as DataGridRow;

    /// <summary>Где на списке строки: под шапкой столбцов и до низа видимой части.</summary>
    private (double Top, double Bottom) RowsArea()
    {
        if (FindChild<ScrollContentPresenter>(List) is { } rows)
        {
            var top = rows.TranslatePoint(default, List).Y;
            return (top, top + rows.ActualHeight);
        }

        return (0, List.ActualHeight);
    }

    // ── галочки и проводник Windows ─────────────────────────────────────────

    /// <summary>Галочка в шапке: что-то не выделено — выделить всё, иначе снять.</summary>
    private void OnCheckAll(object sender, RoutedEventArgs e)
    {
        var (picked, total) = CheckCounts();

        if (picked < total)
        {
            List.SelectAll();

            foreach (var parent in List.SelectedItems.OfType<PaneRow>().Where(r => r.IsParent).ToList())
            {
                List.SelectedItems.Remove(parent);
            }
        }
        else
        {
            List.UnselectAll();
        }

        UpdateAllCheck();
    }

    private (int Picked, int Total) CheckCounts() =>
        (List.SelectedItems.OfType<PaneRow>().Count(r => !r.IsParent), List.Items.OfType<PaneRow>().Count(r => !r.IsParent));

    private void UpdateAllCheck()
    {
        var (picked, total) = CheckCounts();
        AllCheck.IsChecked = picked == 0 ? false : picked == total ? true : null;
    }

    /// <summary>
    /// Системное окно «Открыть папку», как «Файл → Открыть папку» в VS Code: открывается там,
    /// где стоит панель, выбранная папка открывается в панели.
    /// </summary>
    private async void OnOpenInWindows(object sender, RoutedEventArgs e)
    {
        if (_source is not { IsRemote: false })
        {
            return;
        }

        var owner = Window.GetWindow(this) is { } window ? new System.Windows.Interop.WindowInteropHelper(window).Handle : IntPtr.Zero;

        if (LocalPicker.PickFolder(owner, _current) is not { } folder)
        {
            return;
        }

        FileMaskBox.Text = string.Empty;
        await OpenFolderAsync(folder);
        FocusList();
    }

    // ── поиск в визуальном дереве ────────────────────────────────────────────

    private static T? FindChild<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is T found)
            {
                return found;
            }

            if (FindChild<T>(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    private static T? FindChild<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is T found && found.Name == name)
            {
                return found;
            }

            if (FindChild<T>(child, name) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    private static T? FindParent<T>(DependencyObject? node)
        where T : DependencyObject
    {
        while (node is not null and not T)
        {
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }

        return node as T;
    }
}
