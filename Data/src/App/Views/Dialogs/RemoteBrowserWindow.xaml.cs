using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MultiServerSync.Core;
using MultiServerSync.Models;
using MultiServerSync.Services;
using Renci.SshNet.Common;
using Wpf.Ui.Controls;

namespace MultiServerSync.Views.Dialogs;

/// <summary>Узел дерева папок сервера. Дети подгружаются при раскрытии.</summary>
public sealed class RemoteNode : ObservableObject
{
    private bool _isExpanded;
    private bool _isSelected;
    private bool _isVisible = true;

    public RemoteNode(string name, string fullPath)
    {
        Name = name;
        FullPath = fullPath;
        Children = new ObservableCollection<RemoteNode> { new("…", string.Empty, isPlaceholder: true) };
    }

    private RemoteNode(string name, string fullPath, bool isPlaceholder)
    {
        Name = name;
        FullPath = fullPath;
        IsPlaceholder = isPlaceholder;
        Children = new ObservableCollection<RemoteNode>();
    }

    public string Name { get; }

    public string FullPath { get; }

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

    /// <summary>Подходит под маску папок сам или кто-то под ним.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => Set(ref _isVisible, value);
    }

    public ObservableCollection<RemoteNode> Children { get; }
}

/// <summary>Строка списка папки. «..» — тоже строка: по ней поднимаются на уровень вверх.</summary>
public sealed class ListingItem
{
    public required string Name { get; init; }

    public required string FullPath { get; init; }

    public required bool IsDirectory { get; init; }

    public long Length { get; init; }

    public DateTime Modified { get; init; }

    public string Permissions { get; init; } = string.Empty;

    public bool IsLink { get; init; }

    public bool IsParent { get; init; }

    public SymbolRegular Symbol => IsParent
        ? SymbolRegular.ArrowUp24
        : IsDirectory ? SymbolRegular.Folder24 : SymbolRegular.Document24;

    public Visibility LinkVisibility => IsLink ? Visibility.Visible : Visibility.Collapsed;

    public string SizeText => IsDirectory || IsParent ? string.Empty : TransferItem.Format(Length);

    public string ModifiedText => !IsParent && Modified.Year > 1970 ? Modified.ToString("dd.MM.yyyy HH:mm") : string.Empty;
}

/// <summary>
/// Выбор папки на сервере, по образцу WinSCP и проводника: адресная строка, назад/вперёд/вверх,
/// дерево слева, список с «..» и сортировкой справа. Подключение одно на всё окно,
/// прочитанные папки кэшируются — переходы не ждут нового входа на сервер.
/// </summary>
public partial class RemoteBrowserWindow : FluentWindow
{
    private readonly string _initialPath;
    private readonly Stack<string> _back = new();
    private readonly Stack<string> _forward = new();

    private RemoteSession _session;
    private RemoteNode? _root;
    private RemoteNode? _treeSelected;
    private string _current = "/";
    private int _navVersion;
    private bool _syncingTree;
    private bool _ready;

    // сортировка списка: по имени, папки всегда впереди — как в WinSCP
    private string _sortKey = nameof(ListingItem.Name);
    private ListSortDirection _sortDirection = ListSortDirection.Ascending;

    /// <summary>Выбор нескольких файлов и папок — для скачивания; иначе выбирается одна папка.</summary>
    private readonly bool _pickMany;

    public RemoteBrowserWindow(ServerEntry server, string initialPath, bool pickMany = false)
    {
        InitializeComponent();

        _pickMany = pickMany;

        if (pickMany)
        {
            Title = "Что скачать с сервера";
            Listing.SelectionMode = DataGridSelectionMode.Extended;
        }

        Server = server;
        _initialPath = string.IsNullOrWhiteSpace(initialPath) ? "/" : initialPath;
        _session = RemoteSession.Acquire(server, AppState.Current.Settings);

        // в списке — серверы выбранного набора (или всех, если слева «Все»); открытый есть в любом случае
        var servers = AppState.Current.PaneServers.ToList();

        if (!servers.Contains(server))
        {
            servers.Insert(0, server);
        }

        ServerBox.ItemsSource = servers;
        ServerBox.SelectedItem = server;

        PreviewKeyDown += OnWindowKeyDown;
        // подключение и прочитанные папки остаются в запасе: следующее открытие — без входа
        Closed += (_, _) => _session.Release();

        Loaded += async (_, _) =>
        {
            _ready = true;
            await OpenAsync(_initialPath);
        };
    }

    public ServerEntry Server { get; private set; }

    public string? SelectedPath { get; private set; }

    /// <summary>Выбранное в режиме скачивания: выделенные файлы и папки, а если ничего — открытая папка.</summary>
    public IReadOnlyList<string> SelectedPaths { get; private set; } = [];

    /// <summary>То же со строками обзора: тип, размер и дата — для таблицы скачивания.</summary>
    public IReadOnlyList<ListingItem> SelectedItems { get; private set; } = [];

    // ── открытие ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Первый заход на сервер. Папки из пути вкладки может ещё не быть — при копировании она
    /// создастся сама. Тогда открываем ближайшую существующую, а не пустой список с ошибкой.
    /// </summary>
    private async Task OpenAsync(string path)
    {
        ResetTree();
        _back.Clear();
        _forward.Clear();

        var session = _session;

        // папка уже прочитана в прошлый раз — показываем сразу, проверка свежести сама подключится
        if (!session.IsCached(SshService.NormalizeRemote(path)))
        {
            BusyText.Text = $"подключение к {session.Server.Display} …";
        }

        try
        {
            if (!session.IsCached(SshService.NormalizeRemote(path)))
            {
                await session.ConnectAsync();
            }
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(session, _session))
            {
                BusyText.Text = "не удалось подключиться: " + ex.Message;
                AppLog.Error("Обзор папок: " + ex.Message, session.Server.Display);
            }

            return;
        }

        var target = SshService.NormalizeRemote(path);

        while (true)
        {
            var outcome = await NavigateAsync(target, record: false);

            if (outcome != NavResult.Missing || target == "/" || !ReferenceEquals(session, _session))
            {
                break;
            }

            target = SshService.ParentRemote(target);
        }

        var wanted = SshService.NormalizeRemote(path);

        if (ReferenceEquals(session, _session) && _current != wanted)
        {
            BusyText.Text = $"Папки {wanted} на сервере пока нет — она будет создана при копировании. "
                + $"Открыта ближайшая существующая: {_current}";

            // выбор по умолчанию — тот же путь, что был: молча менять его на родителя нельзя
            ChoiceText.Text = wanted;
            _missingChoice = wanted;
        }
    }

    /// <summary>Путь вкладки, которого ещё нет на сервере: он остаётся выбором, пока человек не ушёл.</summary>
    private string? _missingChoice;

    private enum NavResult
    {
        Ok,
        Missing,
        Failed,
        Stale,
    }

    private async Task<NavResult> NavigateAsync(string path, bool record = true, bool refresh = false)
    {
        var target = SshService.NormalizeRemote(path);
        var version = ++_navVersion;
        var session = _session;

        if (refresh || !session.IsCached(target))
        {
            BusyText.Text = $"чтение {target} …";
        }

        // из кэша, но давно — показать сразу и тихо перечитать, как у WinSCP
        var revalidate = !refresh && session.IsCached(target) && !session.IsFresh(target);

        IReadOnlyList<RemoteEntry> items;

        try
        {
            items = await session.ListAsync(target, refresh);
        }
        catch (Exception ex)
        {
            if (version != _navVersion || !ReferenceEquals(session, _session))
            {
                return NavResult.Stale;
            }

            ShowPath(_current);

            if (ex is SftpPathNotFoundException)
            {
                BusyText.Text = $"Папки {target} нет на сервере.";
                return NavResult.Missing;
            }

            BusyText.Text = ex is SftpPermissionDeniedException
                ? $"Нет прав на чтение {target}."
                : "ошибка: " + ex.Message;

            AppLog.Error($"Обзор {target}: {ex.Message}", session.Server.Display);
            return NavResult.Failed;
        }

        // пока читали, человек ушёл в другую папку или на другой сервер — этот ответ уже не нужен
        if (version != _navVersion || !ReferenceEquals(session, _session))
        {
            return NavResult.Stale;
        }

        if (record && _current != target)
        {
            _back.Push(_current);
            _forward.Clear();
        }

        _current = target;
        _missingChoice = null;
        ShowPath(target);

        Show(target, items);
        UpdateNavButtons();

        var dirs = items.Count(i => i.IsDirectory);
        BusyText.Text = $"{session.Server.Display} · папок: {dirs}, файлов: {items.Count - dirs}";

        _ = RevealInTreeAsync(target, version);

        // подпапки — заранее, одной командой: щелчок по любой покажет её сразу
        _ = session.PrefetchFastAsync(items
            .Where(i => i.IsDirectory)
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .Take(300)
            .Select(i => i.FullPath)
            .ToList());

        if (revalidate)
        {
            _ = RevalidateAsync(target, items, version);
        }

        return NavResult.Ok;
    }

    /// <summary>
    /// Папка показана из кэша — перечитываем её в фоне. Изменилась — список обновляется,
    /// не изменилась — человек ничего не замечает. Ошибку здесь не показываем: список уже на экране.
    /// </summary>
    private async Task RevalidateAsync(string path, IReadOnlyList<RemoteEntry> shown, int version)
    {
        var session = _session;
        IReadOnlyList<RemoteEntry> fresh;

        try
        {
            fresh = await session.ListAsync(path, refresh: true);
        }
        catch
        {
            return;
        }

        if (version != _navVersion || !ReferenceEquals(session, _session) || _current != path || Same(shown, fresh))
        {
            return;
        }

        Show(path, fresh);

        var dirs = fresh.Count(i => i.IsDirectory);
        BusyText.Text = $"{session.Server.Display} · папок: {dirs}, файлов: {fresh.Count - dirs}";
    }

    private static bool Same(IReadOnlyList<RemoteEntry> a, IReadOnlyList<RemoteEntry> b) =>
        a.Count == b.Count
        && a.Zip(b).All(p => p.First.FullPath == p.Second.FullPath
            && p.First.Length == p.Second.Length
            && p.First.Modified == p.Second.Modified
            && p.First.IsDirectory == p.Second.IsDirectory);

    private void Show(string path, IReadOnlyList<RemoteEntry> items)
    {
        var rows = new List<ListingItem>(items.Count + 1);

        if (path != "/")
        {
            rows.Add(new ListingItem
            {
                Name = "..",
                FullPath = SshService.ParentRemote(path),
                IsDirectory = true,
                IsParent = true,
            });
        }

        rows.AddRange(items.Select(x => new ListingItem
        {
            Name = x.Name,
            FullPath = x.FullPath,
            IsDirectory = x.IsDirectory,
            Length = x.Length,
            Modified = x.Modified,
            Permissions = x.Permissions,
            IsLink = x.IsLink,
        }));

        Listing.ItemsSource = new ListCollectionView(rows)
        {
            CustomSort = new ListingOrder(_sortKey, _sortDirection),
            Filter = FileFilter,
        };
        ShowSortArrow();
        UpdateChoice();
    }

    // ── маски ──────────────────────────────────────────────────────────────────

    /// <summary>Маска файлов: «..» и папки видны всегда — по ним ходят.</summary>
    private bool FileFilter(object row) =>
        row is not ListingItem item || item.IsParent || item.IsDirectory || NameMask.Matches(item.Name, FileMaskBox.Text);

    private void OnFileMaskChanged(object sender, TextChangedEventArgs e)
    {
        FileMaskClear.Visibility = NameMask.IsEmpty(FileMaskBox.Text) ? Visibility.Collapsed : Visibility.Visible;

        if (Listing.ItemsSource is ListCollectionView view)
        {
            view.Refresh();
        }
    }

    private void OnFolderMaskChanged(object sender, TextChangedEventArgs e)
    {
        FolderMaskClear.Visibility = NameMask.IsEmpty(FolderMaskBox.Text) ? Visibility.Collapsed : Visibility.Visible;
        ApplyFolderMask();
    }

    private void OnClearFileMask(object sender, RoutedEventArgs e) => FileMaskBox.Text = string.Empty;

    private void OnClearFolderMask(object sender, RoutedEventArgs e) => FolderMaskBox.Text = string.Empty;

    /// <summary>
    /// В дереве — подходящие папки, всё под ними и путь к ним сверху. Ищется среди уже
    /// прочитанных веток: дерево сервера целиком не читается.
    /// </summary>
    private void ApplyFolderMask()
    {
        if (_root is not { } root)
        {
            return;
        }

        var mask = FolderMaskBox.Text;
        Visit(root, underMatch: false);

        // вернёт, есть ли совпадение в этой ветке; под подходящей папкой видно всё
        bool Visit(RemoteNode node, bool underMatch)
        {
            var isRoot = ReferenceEquals(node, root);
            var self = !isRoot && NameMask.Matches(node.Name, mask);
            var below = false;

            foreach (var child in node.Children.Where(c => !c.IsPlaceholder))
            {
                below |= Visit(child, underMatch || self);
            }

            node.IsVisible = isRoot || underMatch || self || below;
            return self || below;
        }
    }

    // ── сортировка ─────────────────────────────────────────────────────────────

    private void OnListingSorting(object sender, DataGridSortingEventArgs e)
    {
        // своя сортировка: «..» всегда сверху и папки перед файлами при любом столбце
        e.Handled = true;

        var key = e.Column.SortMemberPath;

        _sortDirection = key == _sortKey && _sortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
        _sortKey = key;

        if (Listing.ItemsSource is ListCollectionView view)
        {
            view.CustomSort = new ListingOrder(_sortKey, _sortDirection);
        }

        ShowSortArrow();
    }

    private void ShowSortArrow()
    {
        foreach (var column in Listing.Columns)
        {
            column.SortDirection = column.SortMemberPath == _sortKey ? _sortDirection : null;
        }
    }

    private sealed class ListingOrder(string key, ListSortDirection direction) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not ListingItem a || y is not ListingItem b)
            {
                return 0;
            }

            if (a.IsParent != b.IsParent)
            {
                return a.IsParent ? -1 : 1;
            }

            if (a.IsDirectory != b.IsDirectory)
            {
                return a.IsDirectory ? -1 : 1;
            }

            var order = key switch
            {
                nameof(ListingItem.Length) => a.Length.CompareTo(b.Length),
                nameof(ListingItem.Modified) => a.Modified.CompareTo(b.Modified),
                nameof(ListingItem.Permissions) => string.CompareOrdinal(a.Permissions, b.Permissions),
                _ => 0,
            };

            if (order == 0 && key != nameof(ListingItem.Name))
            {
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            }

            if (order == 0)
            {
                order = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            }

            return direction == ListSortDirection.Descending ? -order : order;
        }
    }

    // ── дерево ─────────────────────────────────────────────────────────────────

    private void ResetTree()
    {
        _root = new RemoteNode("/", "/");
        _treeSelected = null;
        Tree.ItemsSource = new ObservableCollection<RemoteNode> { _root };
    }

    private async Task LoadChildrenAsync(RemoteNode node)
    {
        if (node.IsLoaded || node.IsPlaceholder)
        {
            return;
        }

        node.IsLoaded = true;

        IReadOnlyList<RemoteEntry> items;

        try
        {
            items = await _session.ListAsync(node.FullPath);
        }
        catch
        {
            // нет прав или папка пропала — ветка просто пустая
            items = [];
        }

        node.Children.Clear();

        foreach (var dir in items
            .Where(c => c.IsDirectory)
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            var child = new RemoteNode(dir.Name, dir.FullPath);
            MarkIfEmpty(child);
            node.Children.Add(child);
        }

        if (!NameMask.IsEmpty(FolderMaskBox.Text))
        {
            ApplyFolderMask();
        }

        _ = PrefetchChildrenAsync(node);
    }

    /// <summary>Стрелка — только у папки, где есть подпапки: ложная стрелка обещает то, чего нет.</summary>
    private void MarkIfEmpty(RemoteNode node)
    {
        if (node.IsLoaded)
        {
            return;
        }

        var empty = _session.HasSubdirs(node.FullPath) is { } has
            ? !has
            : _session.Cached(node.FullPath) is { } items && !items.Any(i => i.IsDirectory);

        if (empty)
        {
            node.IsLoaded = true;
            node.Children.Clear();
        }
    }

    /// <summary>
    /// Обычно чтение папки уже сказало, у каких подпапок есть свои. Где не сказало (сервер без
    /// полноценного find) — подпапки читаются в фоне, чтобы узнать, у каких стрелка не нужна.
    /// </summary>
    private async Task PrefetchChildrenAsync(RemoteNode node)
    {
        var session = _session;
        var children = node.Children
            .Where(c => !c.IsPlaceholder && !c.IsLoaded && session.HasSubdirs(c.FullPath) is null)
            .ToList();

        if (children.Count == 0)
        {
            return;
        }

        await session.PrefetchAsync(children.Select(c => c.FullPath).ToList());

        if (!ReferenceEquals(session, _session))
        {
            return;
        }

        foreach (var child in children)
        {
            MarkIfEmpty(child);
        }
    }

    private async void OnNodeExpanded(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TreeViewItem { DataContext: RemoteNode node })
        {
            await LoadChildrenAsync(node);
        }
    }

    /// <summary>Разворачивает дерево до открытой папки и подсвечивает её — как в проводнике.</summary>
    private async Task RevealInTreeAsync(string path, int version)
    {
        if (_root is not { } node)
        {
            return;
        }

        var session = _session;
        var chain = new List<RemoteNode> { node };

        try
        {
            foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                await LoadChildrenAsync(node);

                if (version != _navVersion || !ReferenceEquals(session, _session))
                {
                    return;
                }

                node.IsExpanded = true;

                var next = node.Children.FirstOrDefault(c => c.Name == segment);

                if (next is null)
                {
                    break;
                }

                node = next;
                chain.Add(node);
            }

            _syncingTree = true;

            if (_treeSelected is { } previous && !ReferenceEquals(previous, node))
            {
                previous.IsSelected = false;
            }

            node.IsSelected = true;
            _treeSelected = node;
            ScrollTreeTo(chain);
        }
        finally
        {
            _syncingTree = false;
        }
    }

    /// <summary>
    /// Дерево прокручивается к открытой папке, если её не видно: она встаёт чуть ниже середины,
    /// чтобы сверху было видно, откуда она, — как в проводнике.
    /// </summary>
    private void ScrollTreeTo(IReadOnlyList<RemoteNode> chain)
    {
        // ветки только что развернулись — строки дерева появятся после раскладки
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            ItemsControl parent = Tree;
            System.Windows.Controls.TreeViewItem? item = null;

            foreach (var node in chain)
            {
                parent.UpdateLayout();
                item = parent.ItemContainerGenerator.ContainerFromItem(node) as System.Windows.Controls.TreeViewItem;

                if (item is null)
                {
                    return;
                }

                parent = item;
            }

            if (item is null || Find<ScrollViewer>(Tree) is not { } scroller)
            {
                return;
            }

            if (scroller.CanContentScroll)
            {
                item.BringIntoView();
                return;
            }

            const double row = 24;
            var top = item.TransformToAncestor(scroller).Transform(new Point(0, 0)).Y;

            if (top >= 0 && top + row <= scroller.ViewportHeight)
            {
                return;
            }

            scroller.ScrollToVerticalOffset(Math.Max(0, scroller.VerticalOffset + top - scroller.ViewportHeight * 0.55));
        });
    }

    private static T? Find<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is T found)
            {
                return found;
            }

            if (Find<T>(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    private async void OnTreeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not RemoteNode { IsPlaceholder: false } node)
        {
            return;
        }

        _treeSelected = node;

        if (_syncingTree || node.FullPath == _current)
        {
            return;
        }

        await NavigateAsync(node.FullPath);
    }

    // ── навигация ──────────────────────────────────────────────────────────────

    private async void OnServerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || ServerBox.SelectedItem is not ServerEntry picked || ReferenceEquals(picked, Server))
        {
            return;
        }

        Server = picked;

        _session.Release();
        _session = RemoteSession.Acquire(picked, AppState.Current.Settings);

        await OpenAsync(_missingChoice ?? _current);
    }

    private async void OnBack(object sender, RoutedEventArgs e) => await GoBackAsync();

    private async void OnForward(object sender, RoutedEventArgs e) => await GoForwardAsync();

    private async Task GoBackAsync()
    {
        if (_back.Count == 0)
        {
            return;
        }

        var from = _current;
        var to = _back.Pop();

        if (await NavigateAsync(to, record: false) == NavResult.Ok)
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
        var to = _forward.Pop();

        if (await NavigateAsync(to, record: false) == NavResult.Ok)
        {
            _back.Push(from);
            UpdateNavButtons();
        }
    }

    private async void OnUp(object sender, RoutedEventArgs e) => await GoUpAsync();

    private async Task GoUpAsync()
    {
        if (_current != "/")
        {
            await NavigateAsync(SshService.ParentRemote(_current));
        }
    }

    private async void OnHome(object sender, RoutedEventArgs e) =>
        await NavigateAsync(_session.Home ?? "/");

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        // перечитывается и сама папка, и дерево: там могли появиться новые ветки
        ResetTree();
        await NavigateAsync(_current, record: false, refresh: true);
    }

    private async void OnPathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await NavigateAsync(PathBox.Text);
            Listing.Focus();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            ShowPath(_current);
            Listing.Focus();
        }
    }

    // ── адресная строка: части пути кнопками, как в WinSCP ──────────────────────

    /// <summary>Пишет путь в поле ввода и пересобирает кнопки частей пути.</summary>
    private void ShowPath(string path)
    {
        PathBox.Text = path;
        Crumbs.Children.Clear();
        Crumbs.Children.Add(MakeCrumb("/", "/"));

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var built = string.Empty;

        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0)
            {
                var slash = new System.Windows.Controls.TextBlock
                {
                    Text = "/",
                    FontFamily = PathBox.FontFamily,
                    FontSize = 14,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)FindResource("TextFillColorTertiaryBrush"),
                };

                // промах мимо кнопки на разделителе — не повод открывать ввод
                slash.MouseLeftButtonDown += (_, e) => e.Handled = true;
                Crumbs.Children.Add(slash);
            }

            built += "/" + parts[i];
            Crumbs.Children.Add(MakeCrumb(parts[i], built));
        }

        // длинный путь не влезает — видна его конечная часть, как в проводнике
        CrumbScroll.ScrollToRightEnd();
    }

    private System.Windows.Controls.Button MakeCrumb(string text, string target)
    {
        var button = new System.Windows.Controls.Button
        {
            Content = text,
            Style = (Style)FindResource("CrumbButtonStyle"),
            ToolTip = target,
        };

        button.Click += async (_, _) =>
        {
            if (target != _current)
            {
                await NavigateAsync(target);
            }
        };

        return button;
    }

    private void OnCrumbBarClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        BeginPathEdit();
    }

    private void BeginPathEdit()
    {
        PathBox.Text = _current;
        CrumbBar.Visibility = Visibility.Collapsed;
        PathBox.Visibility = Visibility.Visible;

        // фокус — после того как поле показалось, иначе он не встаёт
        Dispatcher.BeginInvoke(() =>
        {
            PathBox.Focus();
            PathBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnPathLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // фокус ушёл внутрь самого поля (кнопка очистки) — ввод продолжается
        if (PathBox.IsKeyboardFocusWithin)
        {
            return;
        }

        PathBox.Text = _current;
        PathBox.Visibility = Visibility.Hidden;
        CrumbBar.Visibility = Visibility.Visible;
    }

    private void UpdateNavButtons()
    {
        BackButton.IsEnabled = _back.Count > 0;
        ForwardButton.IsEnabled = _forward.Count > 0;
    }

    private async void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        if ((ctrl && key == Key.L) || key == Key.F4)
        {
            e.Handled = true;
            BeginPathEdit();
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
        else if (alt && key == Key.Up)
        {
            e.Handled = true;
            await GoUpAsync();
        }
        else if (key == Key.F5)
        {
            e.Handled = true;
            await RefreshAsync();
        }
    }

    private async void OnListingKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Listing.SelectedItem is ListingItem { IsDirectory: true } item)
        {
            e.Handled = true;
            await NavigateAsync(item.FullPath);
        }
        else if (e.Key == Key.Back)
        {
            e.Handled = true;
            await GoUpAsync();
        }
    }

    private async void OnListingDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // двойной щелчок по шапке или пустому месту — не переход
        if (FindRow(e.OriginalSource as DependencyObject)?.Item is ListingItem { IsDirectory: true } item)
        {
            await NavigateAsync(item.FullPath);
        }
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

    private async void OnNewFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new InputDialog("Новая папка", $"Создать внутри {_current}:") { Owner = this };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Value))
        {
            return;
        }

        var target = SshService.CombineRemote(_current, dialog.Value.Trim());

        try
        {
            BusyText.Text = "создание папки…";
            await _session.CreateDirectoryAsync(target);
            AppLog.Success($"Создана папка {target}", Server.Display);

            ResetTree();
            await NavigateAsync(_current, record: false, refresh: true);

            // новую папку сразу выделяем — «Выбрать» возьмёт её
            if (Listing.ItemsSource is ListCollectionView view
                && view.Cast<ListingItem>().FirstOrDefault(i => i.FullPath == target) is { } created)
            {
                Listing.SelectedItem = created;
                Listing.ScrollIntoView(created);
            }
        }
        catch (Exception ex)
        {
            BusyText.Text = "ошибка: " + ex.Message;
            AppLog.Error($"Не удалось создать {target}: {ex.Message}", Server.Display);
        }
    }

    // ── выбор ──────────────────────────────────────────────────────────────────

    private void OnListingSelected(object sender, SelectionChangedEventArgs e) => UpdateChoice();

    /// <summary>Выделенная в списке папка, а если не выделена — открытая.</summary>
    private string Choice =>
        _pickMany
            ? Picked.Count switch
            {
                0 => _current,
                1 => Picked[0].FullPath,
                var n => $"{n} объектов в {_current}",
            }
            : Listing.SelectedItem is ListingItem { IsDirectory: true, IsParent: false } item
                ? item.FullPath
                : _missingChoice ?? _current;

    /// <summary>Выделенное в режиме скачивания: и файлы, и папки, без «..».</summary>
    private List<ListingItem> Picked => Listing.SelectedItems
        .OfType<ListingItem>()
        .Where(i => !i.IsParent)
        .ToList();

    private void UpdateChoice() => ChoiceText.Text = Choice;

    private void OnChoose(object sender, RoutedEventArgs e)
    {
        if (_pickMany)
        {
            var picked = Picked;

            // ничего не выделено — берётся открытая папка
            SelectedItems = picked.Count > 0
                ? picked
                : [new ListingItem { Name = _current, FullPath = _current, IsDirectory = true }];
            SelectedPaths = SelectedItems.Select(i => SshService.NormalizeRemote(i.FullPath)).ToList();
            DialogResult = true;
            Close();
            return;
        }

        SelectedPath = SshService.NormalizeRemote(Choice);
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
