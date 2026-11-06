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

/// <summary>
/// Раскладка окон — одна у «Копирования» и «Скачивания»: таблица общая на все окна, строки окна — группой под
/// разделителем с номером; строка пути под таблицей одна — окна в фокусе.
/// </summary>
public static class CopyWindowLayout
{
    /// <summary>Полоса плюса под последней строкой — у единственного окна; при нескольких плюс и минус на разделителях.</summary>
    private const double ButtonsHeight = 26;

    /// <summary>
    /// Окно могло оказаться за краем — таблица подкручивается так, чтобы были видны все его строки с разделителем
    /// (не влезают — с верхней). <paramref name="focusRow"/> — курсор на верхнюю строку окна.
    /// </summary>
    public static void Show(DataGrid grid, WindowRows rows, CopyWindow window, bool focusRow = false) =>
        grid.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (rows.LastRowOf(window) is not { } last)
            {
                return;
            }

            // у свёрнутого окна строки спрятаны — виден только разделитель
            if (!window.IsCollapsed)
            {
                grid.ScrollIntoView(last);
                grid.UpdateLayout();
            }

            if (rows.GroupOf(window) is { } group && grid.ItemContainerGenerator.ContainerFromItem(group) is FrameworkElement box)
            {
                box.BringIntoView();
            }
            else if (rows.FirstRowOf(window) is { } top)
            {
                grid.ScrollIntoView(top);
            }

            if (focusRow && !window.IsCollapsed && rows.FirstRowOf(window) is { } first and not EmptyWindowRow)
            {
                FocusRow(grid, first);
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

    /// <summary>Двойной щелчок по названию на разделителе, и под замком тоже. Пустое название — снова «Окно N».</summary>
    public static void Rename(FrameworkElement plaque, CopyWindow window, Workspace workspace)
    {
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
    /// Кнопки на разделителях окон, щелчок по строке окна или по строке таблицы в нём (фокус) и двойной щелчок по названию — разделитель живёт
    /// в общем словаре стилей, без своего кода, поэтому страница ловит их на таблице по именам.
    /// </summary>
    /// <param name="number">Номер на разделителе: фокус на окне и галочки всех его строк, как галочка в шапке.</param>
    public static void HookGroups(DataGrid grid, WindowRows rows, Action<CopyWindow> number, Action<CopyWindow> focus, Action<CopyWindow> add, Action<CopyWindow> remove, Action<FrameworkElement, CopyWindow> rename)
    {
        grid.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is not FrameworkElement { Name: var name } button || rows.WindowOf(button.DataContext) is not { } window)
            {
                return;
            }

            Action<CopyWindow>? act = name switch
            {
                "GroupNumber" => number,
                "GroupAdd" => add,
                "GroupRemove" => remove,
                "GroupCollapse" => w => Collapse(grid, w),
                _ => null,
            };

            if (act is not null)
            {
                e.Handled = true;
                act(window);
            }
        }));

        grid.PreviewMouseLeftButtonDown += (_, e) =>
        {
            // щелчок по строке окна — по ней самой, галочке или значкам — тоже фокус на её окне; сам щелчок идёт дальше
            if (e.ClickCount == 1)
            {
                for (var node = e.OriginalSource as DependencyObject; node is not null and not DataGrid; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
                {
                    if (node is DataGridRow { Item: { } item } && rows.WindowOf(item) is { } owner)
                    {
                        focus(owner);
                        return;
                    }
                }
            }

            // по названию и адресу окна щелчок только даёт фокус — свернуть и развернуть они не умеют
            var onName = false;
            var onAddress = false;

            for (var node = e.OriginalSource as DependencyObject; node is not null and not DataGrid; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            {
                // плюс и минус — свои действия, номер — фокус своим щелчком
                if (node is ButtonBase)
                {
                    return;
                }

                if (node is FrameworkElement { Name: "GroupName" })
                {
                    onName = true;
                }

                if (node is FrameworkElement { Name: "GroupAddress" })
                {
                    onAddress = true;
                }

                if (e.ClickCount == 2 && node is FrameworkElement { Name: "GroupName" } plaque && rows.WindowOf(plaque.DataContext) is { } named)
                {
                    e.Handled = true;
                    rename(plaque, named);
                    return;
                }

                // строка окна целиком — свернуть или развернуть и фокус на окне; второй щелчок двойного ничего не делает —
                // двойной сворачивает один раз. По адресу — только фокус, его двойной щелчок дальше — «Обзор»
                if (e.ClickCount == 1 && node is FrameworkElement { Name: "Divider" } divider && rows.WindowOf(divider.DataContext) is { } window)
                {
                    if (!onName && !onAddress)
                    {
                        Collapse(grid, window);
                    }

                    focus(window);

                    // курсор — на первую строку окна, как щелчком по ней; у пустого и свёрнутого окна строк не видно
                    if (!window.IsCollapsed && rows.FirstRowOf(window) is { } first and not EmptyWindowRow)
                    {
                        grid.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => FocusRow(grid, first)));
                    }

                    return;
                }
            }
        };
    }

    /// <summary>
    /// Стрелка справа на разделителе: строки окна прячутся под ним и снова видны. Помнит вкладка.
    /// Спрятанные строки таблица не размечает, и развёрнутая строка оставалась с ячейками нулевой ширины —
    /// после переключения строки и столбцы меряются заново.
    /// </summary>
    private static void Collapse(DataGrid grid, CopyWindow window)
    {
        window.IsCollapsed = !window.IsCollapsed;
        AppState.Current.SaveWorkspaces();

        grid.Dispatcher.BeginInvoke(new Action(() => RefitColumns(grid)), DispatcherPriority.Loaded);
    }

    /// <summary>Строка выделена и под курсором клавиатуры: фокус берёт ячейка, строка его не держит.</summary>
    private static void FocusRow(DataGrid grid, object item)
    {
        grid.SelectedItem = item;
        grid.ScrollIntoView(item);

        if (grid.Columns.FirstOrDefault(c => c.Visibility == Visibility.Visible && c.DisplayIndex > 0) is not { } column)
        {
            return;
        }

        grid.CurrentCell = new DataGridCellInfo(item, column);
        grid.UpdateLayout();

        if (column.GetCellContent(item)?.Parent is DataGridCell cell)
        {
            cell.Focus();
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
        var onAddress = false;

        for (var node = e.OriginalSource as DependencyObject; node is not null and not DataGrid; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is ButtonBase or ScrollBar or Thumb or DataGridCell { Column.DisplayIndex: 0 })
            {
                return;
            }

            if (node is FrameworkElement { Name: "GroupAddress" })
            {
                onAddress = true;
            }

            if (node is DataGridRow { Item: { } item })
            {
                action = () => onRow(item);
                break;
            }

            // разделитель окна: строки нет, но окно известно — как пустое место этого окна. Только по адресу:
            // остальная строка окна двойным щелчком сворачивается
            if (node is GroupItem { DataContext: { } group })
            {
                if (!onAddress)
                {
                    return;
                }

                action = () => onRow(group);
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
        SizeColumns.Attach(grid);
        DragChecks.Attach(grid);

        var buttons = area.Children.OfType<StackPanel>().First();

        void Place() => PlaceButtons(grid, area, buttons);

        area.SizeChanged += (_, _) => Place();
        buttons.IsVisibleChanged += (_, _) => Place();
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

        void Refit()
        {
            if (pending)
            {
                return;
            }

            pending = true;

            grid.Dispatcher.BeginInvoke(new Action(() =>
            {
                pending = false;
                RefitColumns(grid);
            }), DispatcherPriority.Background);
        }

        ((INotifyCollectionChanged)grid.Items).CollectionChanged += (_, _) => Refit();
        HeaderFloor(grid);
    }

    /// <summary>
    /// Столбец «по содержимому» меряется по ячейкам строк, а когда строк на виду нет (окна свёрнуты или пусты),
    /// WPF сжимает его до 20 и шапку режет до буквы. Нижняя граница каждого такого столбца — ширина его заголовка.
    /// </summary>
    private static void HeaderFloor(DataGrid grid)
    {
        var face = new Typeface(grid.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var dpi = VisualTreeHelper.GetDpi(grid).PixelsPerDip;

        foreach (var column in grid.Columns.Where(c => c.Width.IsAuto && c.Header is string))
        {
            var text = new FormattedText((string)column.Header, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, face, 11, Brushes.Black, null, TextFormattingMode.Display, dpi);

            // поля шапки 8+8, граница 1, стрелка сортировки 6+8
            column.MinWidth = Math.Ceiling(text.WidthIncludingTrailingWhitespace) + 31;
        }
    }

    private static readonly HashSet<DataGrid> WaitingRefit = new();

    /// <summary>
    /// Скрытая таблица (другая вкладка или раздел) ячеек не мерит: столбцы оставались на минимальной ширине,
    /// а при показе разметка бралась из старой и не пересчитывалась. Замер ждёт показа, ячейки меряются заново.
    /// </summary>
    private static void RefitColumns(DataGrid grid)
    {
        if (!grid.IsVisible)
        {
            if (WaitingRefit.Add(grid))
            {
                DependencyPropertyChangedEventHandler? shown = null;
                shown = (_, _) =>
                {
                    if (!grid.IsVisible)
                    {
                        return;
                    }

                    grid.IsVisibleChanged -= shown;
                    WaitingRefit.Remove(grid);
                    grid.Dispatcher.BeginInvoke(new Action(() => RefitColumns(grid)), DispatcherPriority.Loaded);
                };
                grid.IsVisibleChanged += shown;
            }

            return;
        }

        foreach (var column in grid.Columns.Where(c => c.Width.IsAuto))
        {
            column.Width = new DataGridLength(0);
            column.Width = DataGridLength.Auto;
        }

        foreach (var cells in FindAll<DataGridCellsPresenter>(grid))
        {
            cells.InvalidateMeasure();

            if (FindChild<DataGridCellsPanel>(cells) is { } panel)
            {
                panel.InvalidateMeasure();
            }
        }
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
    /// Плюс единственного окна — сразу под последней строкой. Строки не влезают вместе с ним — у нижнего края
    /// таблицы, а таблица уступает ему полосу: значок не ложится поверх строк. Окон несколько — плюса здесь нет.
    /// </summary>
    private static void PlaceButtons(DataGrid grid, Grid area, FrameworkElement buttons)
    {
        if (!buttons.IsVisible)
        {
            if (grid.Margin != default)
            {
                grid.Margin = default;
            }

            return;
        }

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

    private static IEnumerable<T> FindAll<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);

            if (child is T match)
            {
                yield return match;
            }
            else
            {
                foreach (var deeper in FindAll<T>(child))
                {
                    yield return deeper;
                }
            }
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
