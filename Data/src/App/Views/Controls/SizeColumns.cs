using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MultiServerSync.Services;

namespace MultiServerSync.Views.Controls;

/// <summary>
/// Столбцы «Размер», «Размер*», «Файлов», «Файлов*» общей таблицы окон — одни у «Копирования» и «Скачивания».
/// Исключений нет ни у одного окна — видны «Размер» и «Файлов», меню нет: звёздочка ничего бы не добавила.
/// Исключения есть хоть у одного — видны столбцы со звёздочкой (шапка у окон общая), а меню на шапке
/// включает любые из четырёх; выбор помнит вкладка.
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

        Explain(grid, columns, KeptSize, "С учётом исключений: размер без того, что отсеивают маски окна строки");
        Explain(grid, columns, KeptFiles, "С учётом исключений: файлов без тех, что отсеивают маски окна строки");

        Workspace? tab = null;

        void Apply()
        {
            var shown = tab is { AnyWindowFilters: true } ? Shown(tab) : Plain;

            foreach (var column in columns)
            {
                column.Visibility = shown.Contains((string)column.Header) ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // маски окна в фокусе живут в полях вкладки, у остальных — в самих окнах; окна добавляют и убирают
        void OnTabChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(Workspace.ExcludeMasks) or nameof(Workspace.UseGlobalExcludes) or nameof(Workspace.HasGlobalExcludes)
                or nameof(Workspace.AnyWindowFilters) or nameof(Workspace.SizeColumnsShown) or nameof(Workspace.IsMultiWindow))
            {
                Apply();
            }
        }

        void Bind()
        {
            if (tab is not null)
            {
                tab.PropertyChanged -= OnTabChanged;
            }

            tab = grid.DataContext as Workspace;

            if (tab is not null)
            {
                tab.PropertyChanged += OnTabChanged;
            }

            Apply();
        }

        grid.DataContextChanged += (_, _) => Bind();
        grid.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (tab is { AnyWindowFilters: true } && IsOnHeader(e.OriginalSource as DependencyObject))
            {
                e.Handled = true;
                ShowMenu(grid, tab);
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

    private static IReadOnlyList<string> Shown(Workspace tab) => tab.SizeColumnsShown ?? ByDefault;

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

    private static void ShowMenu(DataGrid grid, Workspace tab)
    {
        var menu = new ContextMenu { PlacementTarget = grid, Placement = PlacementMode.MousePoint };
        var shown = Shown(tab);

        foreach (var name in Names)
        {
            var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = shown.Contains(name) };

            item.Click += (_, _) =>
            {
                var picked = Shown(tab).ToHashSet();

                if (item.IsChecked)
                {
                    picked.Add(name);
                }
                else
                {
                    picked.Remove(name);
                }

                tab.SetSizeColumnsShown(Names.Where(picked.Contains).ToList());
            };

            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }
}
