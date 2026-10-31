using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MultiServerSync.Services;
using MultiServerSync.Views.Dialogs;

namespace MultiServerSync.Views.Controls;

/// <summary>Раскладка окон — одна у «Копирования» и «Скачивания»: высота окон, место плюса и минуса.</summary>
public static class CopyWindowLayout
{
    /// <summary>Ниже этого окно не ужимается: шапка, строка, плюс и строка пути — дальше прокрутка.</summary>
    private const double MinWindowHeight = 150;

    /// <summary>Полоса плюса и минуса под последней строкой.</summary>
    private const double ButtonsHeight = 26;

    /// <summary>Окна делят высоту поровну, но не ниже своего минимума.</summary>
    public static void Follow(ScrollViewer scroll, ItemsControl list)
    {
        void Fit() => list.Height = Math.Max(scroll.ActualHeight, list.Items.Count * MinWindowHeight);

        scroll.SizeChanged += (_, _) => Fit();
        ((INotifyCollectionChanged)list.Items).CollectionChanged += (_, _) => Fit();
    }

    /// <summary>Окна переставляются мышью за шапку таблицы: тот же метод, что у ярлыков. Содержимое не меняется, поэтому и под замком.</summary>
    public static void Reorder(ScrollViewer scroll, ItemsControl list, Func<Workspace?> tab) =>
        _ = new TabReorder(
            list,
            scroll,
            item => item is CopyWindow { IsMulti: true },
            fixedCount: 0,
            (item, to) =>
            {
                if (item is CopyWindow window)
                {
                    tab()?.MoveCopyWindow(window, to);
                }
            },
            grabButtons: true)
        {
            GrabPlace = IsTableHeader,
        };

    /// <summary>Шапка таблицы, кроме её галочки «отметить все».</summary>
    private static bool IsTableHeader(DependencyObject source)
    {
        for (var node = source; node is not null and not DataGrid; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is CheckBox)
            {
                return false;
            }

            if (node is DataGridColumnHeader)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Новое окно могло встать ниже видимого — показать его.</summary>
    public static void Show(ItemsControl list, object window) =>
        list.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (list.ItemContainerGenerator.ContainerFromItem(window) is FrameworkElement shown)
            {
                shown.BringIntoView();
            }
        }), DispatcherPriority.Loaded);

    /// <summary>
    /// Красная рамка окна не гаснет, пока человек её не увидел: на другой вкладке, в другом разделе или
    /// в другой программе она ждёт. На глазах держится недолго.
    /// </summary>
    public static void WatchFailures(FrameworkElement page, Func<Workspace?> tab)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        var hooked = false;

        void Check()
        {
            if (page.IsVisible
                && Window.GetWindow(page) is { IsActive: true }
                && tab()?.CopyWindows.Any(w => w.IsFailed) == true
                && !timer.IsEnabled)
            {
                timer.Start();
            }
        }

        timer.Tick += (_, _) =>
        {
            timer.Stop();

            if (page.IsVisible && tab() is { } workspace)
            {
                foreach (var window in workspace.CopyWindows)
                {
                    window.IsFailed = false;
                }
            }
        };

        page.IsVisibleChanged += (_, _) => Check();
        CopyWindow.FailureShown += Check;
        AppState.Current.ActiveWorkspaceChanged += _ => Check();

        page.Loaded += (_, _) =>
        {
            if (!hooked && Window.GetWindow(page) is { } owner)
            {
                hooked = true;
                owner.Activated += (_, _) => Check();
            }

            Check();
        };
    }

    /// <summary>Двойной щелчок по плашке названия, и под замком тоже. Пустое название — снова «Окно N».</summary>
    public static void Rename(FrameworkElement plaque, Workspace workspace)
    {
        if (plaque.DataContext is not CopyWindow window)
        {
            return;
        }

        var dialog = new InputDialog("Название окна", $"Название окна (пусто — «Окно {window.Number}»):", window.Name)
        {
            Owner = Window.GetWindow(plaque),
        };

        if (dialog.ShowDialog() == true)
        {
            workspace.RenameWindow(window, dialog.Value);
        }
    }

    /// <summary>
    /// Двойной щелчок в таблице окна: мимо строк — как кнопка добавления, по строке — замена её адреса.
    /// Кнопки, галочки с их ячейкой, шапка и прокрутка заняты своим. Выбор открывается, когда нажатие уже
    /// обработано: иначе окно выбора вставало бы посреди него.
    /// </summary>
    public static void DoubleClick(MouseButtonEventArgs e, Action onEmpty, Action<object> onRow)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        Action? action = onEmpty;

        for (var node = e.OriginalSource as DependencyObject; node is not null and not DataGrid; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is ButtonBase or ScrollBar or Thumb or DataGridCell { Column.DisplayIndex: 0 })
            {
                return;
            }

            if (node is DataGridRow { Item: { } item })
            {
                action = () => onRow(item);
                break;
            }
        }

        Application.Current.Dispatcher.BeginInvoke(action, DispatcherPriority.Input);
    }

    /// <summary>Таблица окна появилась: порядок строк и место плюса с минусом.</summary>
    public static void Arrange(DataGrid grid, string sortKey)
    {
        // страницу открывают снова и снова, а таблица та же
        if (grid.Tag is not null || grid.Parent is not Grid area)
        {
            return;
        }

        grid.Tag = true;

        // порядок в таблице выбирается щелчком по заголовку и не сбрасывается
        GridSort.Keep(grid, sortKey);

        var buttons = area.Children.OfType<StackPanel>().First();

        void Place() => PlaceButtons(grid, area, buttons);

        area.SizeChanged += (_, _) => Place();
        ((INotifyCollectionChanged)grid.Items).CollectionChanged += (_, _) => Place();
        FollowColumns(grid);

        // шапка получает высоту после разметки
        grid.Dispatcher.BeginInvoke(new Action(Place), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Столбцы «по содержимому» сами только растут: строку с длинным именем убрали, а ширина осталась,
    /// и столбец выглядел растянутым руками. При каждой смене строк ширина меряется заново.
    /// </summary>
    public static void FollowColumns(DataGrid grid)
    {
        var pending = false;

        ((INotifyCollectionChanged)grid.Items).CollectionChanged += (_, _) =>
        {
            if (pending)
            {
                return;
            }

            pending = true;

            grid.Dispatcher.BeginInvoke(new Action(() =>
            {
                pending = false;

                foreach (var column in grid.Columns.Where(c => c.Width.IsAuto))
                {
                    column.Width = new DataGridLength(0);
                    column.Width = DataGridLength.Auto;
                }
            }), DispatcherPriority.Background);
        };
    }

    /// <summary>Нижняя полоса страницы открывается такой, какой её оставили.</summary>
    public static void RestoreBand(RowDefinition band, ColumnDefinition left)
    {
        var settings = AppState.Current.Settings;

        if (settings.CopyBandHeight > 0)
        {
            band.Height = new GridLength(settings.CopyBandHeight);
        }

        if (settings.CopyBandLeft > 0)
        {
            left.Width = new GridLength(settings.CopyBandLeft);
        }
    }

    /// <summary>Границу полосы сдвинули мышью.</summary>
    public static void SaveBand(RowDefinition band, ColumnDefinition left)
    {
        var settings = AppState.Current.Settings;

        settings.CopyBandHeight = band.ActualHeight;
        settings.CopyBandLeft = left.ActualWidth;
        AppState.Current.SaveSettings();
    }

    /// <summary>
    /// Плюс и минус — сразу под последней строкой. Строки не влезают вместе с ними — у нижнего края
    /// таблицы, а таблица уступает им полосу: значки не ложатся поверх строк.
    /// </summary>
    private static void PlaceButtons(DataGrid grid, Grid area, FrameworkElement buttons)
    {
        var header = FindChild<DataGridColumnHeadersPresenter>(grid)?.ActualHeight ?? 0;
        var content = header + grid.Items.Count * grid.RowHeight;
        var overflow = content + ButtonsHeight > area.ActualHeight;
        var top = overflow ? area.ActualHeight - ButtonsHeight : content;

        // значки столбиком со значками строки: поле ячейки 8 и отступ 2
        var place = new Thickness(0, Math.Max(0, top) + 2, 10, 0);
        var room = new Thickness(0, 0, 0, overflow ? ButtonsHeight : 0);

        if (buttons.Margin != place)
        {
            buttons.Margin = place;
        }

        if (grid.Margin != room)
        {
            grid.Margin = room;
        }
    }

    private static T? FindChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);

            if (child is T match)
            {
                return match;
            }

            if (FindChild<T>(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }
}
