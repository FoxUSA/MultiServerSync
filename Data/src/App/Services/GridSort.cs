using System.Collections;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Data;

namespace MultiServerSync.Services;

/// <summary>
/// Сортировка таблицы, которая не сбрасывается. Список у каждой вкладки свой: при смене
/// вкладки таблица получает другую коллекцию и порядок теряет, а при закрытии программы
/// он вообще нигде не жил. Выбранный столбец и направление лежат в настройках и ставятся
/// на место — при возврате в раздел, при смене вкладки и при следующем запуске.
/// </summary>
public static class GridSort
{
    /// <summary>
    /// Подключает таблицу: запоминает щелчки по заголовкам и возвращает сохранённый порядок.
    /// </summary>
    /// <param name="key">Под каким именем порядок хранится в настройках.</param>
    /// <param name="arrange">
    /// Своё упорядочивание поверх выбранного столбца: получает сравнение по столбцу и
    /// возвращает итоговое, null — обычная сортировка. Нужно, когда строки идут связками.
    /// </param>
    public static void Keep(DataGrid grid, string key, Func<IComparer, IComparer?>? arrange = null)
    {
        Grids.Add((new WeakReference<DataGrid>(grid), key));

        // щелчок по заголовку сортирует сам — состояние читаем после того, как он это сделал
        grid.Sorting += (_, _) => grid.Dispatcher.BeginInvoke(new Action(() => Save(grid, key, arrange)));

        // смена вкладки подменяет коллекцию: порядок ставим заново, когда привязка обновилась
        DependencyPropertyDescriptor
            .FromProperty(ItemsControl.ItemsSourceProperty, typeof(DataGrid))
            .AddValueChanged(grid, (_, _) => grid.Dispatcher.BeginInvoke(new Action(() => Apply(grid, key, arrange))));

        grid.Loaded += (_, _) => Apply(grid, key, arrange);

        Apply(grid, key, arrange);
    }

    /// <summary>Подключённые таблицы — чтобы сбросить порядок у той, что сейчас не на экране.</summary>
    private static readonly List<(WeakReference<DataGrid> Grid, string Key)> Grids = [];

    /// <summary>
    /// Сортировку по столбцу снять: строки идут в своём порядке. Нужно, когда порядок
    /// переставили руками в другом месте, — иначе таблица его не покажет.
    /// </summary>
    public static void Reset(string key)
    {
        if (AppState.Current.Settings.GridSort.TryGetValue(key, out var stored) && !string.IsNullOrEmpty(stored))
        {
            AppState.Current.Settings.GridSort[key] = string.Empty;
            AppState.Current.SaveSettings();
        }

        foreach (var (reference, gridKey) in Grids)
        {
            if (gridKey != key || !reference.TryGetTarget(out var grid))
            {
                continue;
            }

            grid.Items.SortDescriptions.Clear();
            KeepLive(grid);

            foreach (var column in grid.Columns)
            {
                column.SortDirection = null;
            }
        }
    }

    private static void Save(DataGrid grid, string key, Func<IComparer, IComparer?>? arrange)
    {
        KeepLive(grid);

        // заголовок отсортировал по-обычному — своё упорядочивание ставим обратно
        if (arrange is not null)
        {
            grid.Dispatcher.BeginInvoke(new Action(() => Apply(grid, key, arrange)));
        }

        var stored = AppState.Current.Settings.GridSort;
        var sorting = grid.Items.SortDescriptions;

        var value = sorting.Count == 0
            ? string.Empty
            : sorting[0].PropertyName + (sorting[0].Direction == ListSortDirection.Descending ? "|desc" : "|asc");

        if (stored.TryGetValue(key, out var current) && string.Equals(current, value, StringComparison.Ordinal))
        {
            return;
        }

        stored[key] = value;
        AppState.Current.SaveSettings();
    }

    private static void Apply(DataGrid grid, string key, Func<IComparer, IComparer?>? arrange)
    {
        if (grid.ItemsSource is null
            || !AppState.Current.Settings.GridSort.TryGetValue(key, out var stored)
            || string.IsNullOrWhiteSpace(stored))
        {
            return;
        }

        var parts = stored.Split('|');
        var path = parts[0];
        var direction = parts.Length > 1 && parts[1] == "desc"
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;

        // столбец мог исчезнуть между версиями — тогда порядок остаётся прежним
        if (grid.Columns.FirstOrDefault(c => string.Equals(PathOf(c), path, StringComparison.Ordinal)) is null)
        {
            return;
        }

        try
        {
            var sorting = grid.Items.SortDescriptions;

            if (arrange?.Invoke(new ByProperty(path, direction)) is { } custom
                && CollectionViewSource.GetDefaultView(grid.ItemsSource) is ListCollectionView view)
            {
                // обычная сортировка и своя вместе не живут: сначала убираем обычную
                sorting.Clear();
                view.CustomSort = custom;
                KeepLive(grid, path);
            }
            else
            {
                if (sorting.Count != 1 || sorting[0].PropertyName != path || sorting[0].Direction != direction)
                {
                    sorting.Clear();
                    sorting.Add(new SortDescription(path, direction));
                }

                KeepLive(grid);
            }

            // стрелка в шапке: она живёт у столбца, а не у коллекции
            foreach (var column in grid.Columns)
            {
                column.SortDirection = string.Equals(PathOf(column), path, StringComparison.Ordinal)
                    ? direction
                    : null;
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug($"Не удалось восстановить сортировку таблицы «{key}»: {ex.Message}");
        }
    }

    /// <summary>
    /// Сортировка сама по себе срабатывает один раз: если у строки потом поменялось значение
    /// (дата папки перечиталась с диска после правки файла внутри), строка остаётся на старом
    /// месте. Живая сортировка следит за свойствами, по которым упорядочено, и переставляет строку.
    /// </summary>
    /// <param name="path">Свойство для своего упорядочивания — у него обычной сортировки нет.</param>
    private static void KeepLive(DataGrid grid, string? path = null)
    {
        if (grid.Items is not ICollectionViewLiveShaping { CanChangeLiveSorting: true } live)
        {
            return;
        }

        live.LiveSortingProperties.Clear();

        if (path is not null)
        {
            live.LiveSortingProperties.Add(path);
        }

        foreach (var description in grid.Items.SortDescriptions)
        {
            live.LiveSortingProperties.Add(description.PropertyName);
        }

        live.IsLiveSorting = live.LiveSortingProperties.Count > 0;
    }

    /// <summary>Сравнение строк по свойству столбца — то же, что делает обычная сортировка.</summary>
    private sealed class ByProperty(string path, ListSortDirection direction) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            var order = Comparer.Default.Compare(ValueOf(x), ValueOf(y));
            return direction == ListSortDirection.Descending ? -order : order;
        }

        private object? ValueOf(object? item) => item?.GetType().GetProperty(path)?.GetValue(item);
    }

    /// <summary>
    /// По какому свойству сортируется столбец. У столбцов с надписью вместо значения
    /// путь задан явно (SortMemberPath), у остальных берётся из привязки.
    /// </summary>
    private static string PathOf(DataGridColumn column)
    {
        if (!string.IsNullOrEmpty(column.SortMemberPath))
        {
            return column.SortMemberPath;
        }

        return column is DataGridBoundColumn { Binding: System.Windows.Data.Binding binding }
            ? binding.Path?.Path ?? string.Empty
            : string.Empty;
    }
}
