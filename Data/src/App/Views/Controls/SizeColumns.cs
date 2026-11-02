using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MultiServerSync.Services;

namespace MultiServerSync.Views.Controls;

/// <summary>
/// Столбцы «Размер», «Размер*», «Файлов», «Файлов*» таблицы окна — одни у «Копирования» и «Скачивания».
/// Исключений нет — видны «Размер» и «Файлов», меню нет: звёздочка ничего бы не добавила. Исключения есть —
/// видны столбцы со звёздочкой, а меню на шапке включает любые из четырёх; выбор помнит вкладка.
/// </summary>
public static class SizeColumns
{
    private const string Size = "Размер";
    private const string KeptSize = "Размер*";
    private const string Files = "Файлов";
    private const string KeptFiles = "Файлов*";

    private static readonly string[] Names = [Size, KeptSize, Files, KeptFiles];

    private static readonly string[] Plain = [Size, Files];

    private static readonly string[] ByDefault = [KeptSize, KeptFiles];

    public static void Attach(DataGrid grid)
    {
        var columns = grid.Columns.Where(c => c.Header is string header && Names.Contains(header)).ToList();

        Explain(grid, columns, KeptSize, "С учётом исключений: размер без того, что отсеивают маски этого окна");
        Explain(grid, columns, KeptFiles, "С учётом исключений: файлов без тех, что отсеивают маски этого окна");

        CopyWindow? window = null;

        void Apply()
        {
            var shown = window is { HasFilters: true } ? Shown(window) : Plain;

            foreach (var column in columns)
            {
                column.Visibility = shown.Contains((string)column.Header) ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        void OnWindowChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(CopyWindow.HasFilters) or nameof(CopyWindow.SizeColumnsShown))
            {
                Apply();
            }
        }

        void Bind()
        {
            if (window is not null)
            {
                window.PropertyChanged -= OnWindowChanged;
            }

            window = grid.DataContext as CopyWindow;

            if (window is not null)
            {
                window.PropertyChanged += OnWindowChanged;
            }

            Apply();
        }

        grid.DataContextChanged += (_, _) => Bind();
        grid.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (window is { HasFilters: true } && IsOnHeader(e.OriginalSource as DependencyObject))
            {
                e.Handled = true;
                ShowMenu(grid, window);
            }
        };

        Bind();
    }

    /// <summary>Подсказка на шапке столбца: столбец не в дереве окна, поэтому стилем, на основе общего.</summary>
    private static void Explain(DataGrid grid, List<DataGridColumn> columns, string name, string tip)
    {
        if (columns.FirstOrDefault(c => (string)c.Header == name) is { } column)
        {
            column.HeaderStyle = new Style(typeof(DataGridColumnHeader), grid.ColumnHeaderStyle)
            {
                Setters = { new Setter(FrameworkElement.ToolTipProperty, tip) },
            };
        }
    }

    private static IReadOnlyList<string> Shown(CopyWindow window) => window.SizeColumnsShown ?? ByDefault;

    private static bool IsOnHeader(DependencyObject? node)
    {
        while (node is not null and not DataGridRow)
        {
            if (node is DataGridColumnHeader)
            {
                return true;
            }

            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return false;
    }

    private static void ShowMenu(DataGrid grid, CopyWindow window)
    {
        var menu = new ContextMenu { PlacementTarget = grid, Placement = PlacementMode.MousePoint };
        var shown = Shown(window);

        foreach (var name in Names)
        {
            var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = shown.Contains(name) };

            item.Click += (_, _) =>
            {
                var picked = Shown(window).ToHashSet();

                if (item.IsChecked)
                {
                    picked.Add(name);
                }
                else
                {
                    picked.Remove(name);
                }

                window.SetSizeColumnsShown(Names.Where(picked.Contains).ToList());
            };

            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }
}
