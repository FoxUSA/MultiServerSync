using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using MultiServerSync.Services;

namespace MultiServerSync.Views.Controls;

/// <summary>
/// Строка пустого окна: окон несколько, а в этом пока ничего нет. Свойства — те же, по которым сортируются
/// настоящие строки, и тех же типов: сортировка идёт по всему списку сразу, и чужой тип её ронял бы.
/// </summary>
public sealed class EmptyWindowRow(CopyWindow window, string hint)
{
    public CopyWindow Window { get; } = window;

    /// <summary>Надпись на всю строку: что сделать, чтобы в окне что-то появилось.</summary>
    public string Hint { get; } = hint;

    public string Kind => string.Empty;

    public string Name => string.Empty;

    public string FullPath => string.Empty;

    public string Path => string.Empty;

    public string ServerLabel => string.Empty;

    public long SizeBytes => 0;

    public long KeptBytes => 0;

    public int Files => 0;

    public int FilesCount => 0;

    public int KeptFiles => 0;

    public DateTime Modified => default;
}

/// <summary>
/// Таблица «Копирования» и «Скачивания» одна на все окна вкладки: шапка общая, строки каждого окна — своей
/// группой под тонкой линией с номером и названием. Здесь — строки всех окон одним списком, кому какая
/// принадлежит, и представление с группами по окнам в их порядке.
/// </summary>
public sealed class WindowRows
{
    private readonly bool _downloads;
    private readonly ObservableCollection<object> _rows = [];
    private readonly Dictionary<object, CopyWindow> _owners = [];
    private readonly Dictionary<CopyWindow, EmptyWindowRow> _empty = [];
    private readonly List<CopyWindow> _watched = [];
    private Workspace? _workspace;

    /// <param name="downloads">Строки скачивания, а не копирования.</param>
    public WindowRows(bool downloads) => _downloads = downloads;

    /// <summary>Что показывает таблица: новое на каждую вкладку — сортировка ставится заново по смене источника.</summary>
    public ListCollectionView? View { get; private set; }

    /// <summary>Окно строки, заглушки или заголовка группы.</summary>
    public CopyWindow? WindowOf(object? row) => row switch
    {
        CopyWindow window => window,
        CollectionViewGroup { Name: CopyWindow window } => window,
        EmptyWindowRow empty => empty.Window,
        null => null,
        _ => _owners.GetValueOrDefault(row),
    };

    /// <summary>Группа окна в таблице: её строки — в том порядке, в каком их показывает сортировка.</summary>
    public CollectionViewGroup? GroupOf(CopyWindow window) =>
        View?.Groups?.OfType<CollectionViewGroup>().FirstOrDefault(g => ReferenceEquals(g.Name, window));

    /// <summary>Верхняя строка окна на экране — на неё встаёт курсор.</summary>
    public object? FirstRowOf(CopyWindow window) =>
        GroupOf(window) is { Items.Count: > 0 } group ? group.Items[0] : _rows.FirstOrDefault(r => ReferenceEquals(WindowOf(r), window));

    /// <summary>Нижняя строка окна на экране.</summary>
    public object? LastRowOf(CopyWindow window) =>
        GroupOf(window) is { Items.Count: > 0 } group ? group.Items[group.Items.Count - 1] : FirstRowOf(window);

    /// <summary>Таблица переходит на другую вкладку.</summary>
    public ListCollectionView? Attach(Workspace? workspace)
    {
        if (ReferenceEquals(workspace, _workspace) && View is not null)
        {
            return View;
        }

        if (_workspace is not null)
        {
            _workspace.CopyWindows.CollectionChanged -= OnWindowsChanged;
        }

        Unwatch();
        _workspace = workspace;
        _rows.Clear();
        _owners.Clear();
        _empty.Clear();

        if (workspace is null)
        {
            View = null;
            return null;
        }

        workspace.CopyWindows.CollectionChanged += OnWindowsChanged;
        Watch();
        Sync();

        var view = new ListCollectionView(_rows);
        view.GroupDescriptions.Add(new ByWindow(this) { CustomSort = new ByNumber() });
        View = view;
        return view;
    }

    private bool _refreshPending;

    private void OnWindowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Unwatch();
        Watch();
        Sync();

        // группы идут по номерам окон, а номера вкладка ставит уже после вставки или переноса окна —
        // пересортировать, когда она закончит
        if (_refreshPending)
        {
            return;
        }

        _refreshPending = true;
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            _refreshPending = false;
            View?.Refresh();
        }));
    }

    private void Watch()
    {
        foreach (var window in _workspace?.CopyWindows ?? [])
        {
            ListOf(window).CollectionChanged += OnListChanged;
            _watched.Add(window);
        }
    }

    private void Unwatch()
    {
        foreach (var window in _watched)
        {
            ListOf(window).CollectionChanged -= OnListChanged;
        }

        _watched.Clear();
    }

    private INotifyCollectionChanged ListOf(CopyWindow window) => _downloads ? window.Downloads : window.Items;

    private IEnumerable<object> RowsOf(CopyWindow window) => _downloads ? window.Downloads : window.Items;

    private void OnListChanged(object? sender, NotifyCollectionChangedEventArgs e) => Sync();

    /// <summary>
    /// Список сводится к окнам по месту, без пересборки: пересборка сбрасывала бы прокрутку и выделение.
    /// Пустое окно при нескольких окнах — одной строкой-заглушкой.
    /// </summary>
    private void Sync()
    {
        if (_workspace is not { } workspace)
        {
            return;
        }

        var multi = workspace.CopyWindows.Count > 1;
        var wanted = new List<object>();
        _owners.Clear();

        foreach (var window in workspace.CopyWindows)
        {
            var rows = RowsOf(window).ToList();

            if (rows.Count == 0 && multi)
            {
                if (!_empty.TryGetValue(window, out var empty))
                {
                    empty = new EmptyWindowRow(window, _downloads
                        ? "Пусто — двойной щелчок здесь откроет обзор сервера для этого окна"
                        : "Пусто — двойной щелчок здесь добавит файлы в это окно");
                    _empty[window] = empty;
                }

                rows.Add(empty);
            }

            foreach (var row in rows)
            {
                _owners[row] = window;
                wanted.Add(row);
            }
        }

        foreach (var gone in _empty.Keys.Where(w => !workspace.CopyWindows.Contains(w)).ToList())
        {
            _empty.Remove(gone);
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < _rows.Count && ReferenceEquals(_rows[i], wanted[i]))
            {
                continue;
            }

            var at = IndexOf(wanted[i], i + 1);

            if (at >= 0)
            {
                _rows.Move(at, i);
            }
            else
            {
                _rows.Insert(i, wanted[i]);
            }
        }

        while (_rows.Count > wanted.Count)
        {
            _rows.RemoveAt(_rows.Count - 1);
        }
    }

    private int IndexOf(object row, int from)
    {
        for (var i = from; i < _rows.Count; i++)
        {
            if (ReferenceEquals(_rows[i], row))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Группа строки — её окно.</summary>
    private sealed class ByWindow(WindowRows rows) : GroupDescription
    {
        public override object GroupNameFromItem(object item, int level, CultureInfo culture) =>
            rows.WindowOf(item) ?? (object)string.Empty;
    }

    /// <summary>Группы — в порядке окон, что бы ни выбрали сортировкой строк.</summary>
    private sealed class ByNumber : IComparer
    {
        public int Compare(object? x, object? y) => NumberOf(x).CompareTo(NumberOf(y));

        private static int NumberOf(object? group) => group switch
        {
            CollectionViewGroup { Name: CopyWindow window } => window.Number,
            CopyWindow window => window.Number,
            _ => int.MaxValue,
        };
    }
}

/// <summary>Строка-заглушка пустого окна рисуется своим видом: одна надпись на всю ширину.</summary>
public sealed class WindowRowStyleSelector : StyleSelector
{
    public Style? Row { get; set; }

    public Style? Empty { get; set; }

    public override Style? SelectStyle(object item, DependencyObject container) =>
        item is EmptyWindowRow ? Empty : Row;
}
