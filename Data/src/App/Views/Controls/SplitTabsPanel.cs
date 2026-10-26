using System.Windows;
using System.Windows.Controls;
using MultiServerSync.Services;

namespace MultiServerSync.Views.Controls;

/// <summary>
/// Лента вкладок: обычные — от левого края, личные вкладки команд — прижаты к правому.
/// Пока места хватает, между ними пусто; не хватает — встают вплотную и лента листается.
/// В кратком виде ярлыки идут столбцами по <see cref="Rows"/> штуки сверху вниз, как раньше
/// в вертикальном WrapPanel, — и так же отдельно слева и справа.
/// </summary>
public sealed class SplitTabsPanel : Panel
{
    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(
        nameof(Rows),
        typeof(int),
        typeof(SplitTabsPanel),
        new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty RowHeightProperty = DependencyProperty.Register(
        nameof(RowHeight),
        typeof(double),
        typeof(SplitTabsPanel),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Сколько ярлыков в столбце: 1 — полный вид, 2 — краткий.</summary>
    public int Rows
    {
        get => (int)GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    /// <summary>Высота ряда; не задана — по самому высокому ярлыку.</summary>
    public double RowHeight
    {
        get => (double)GetValue(RowHeightProperty);
        set => SetValue(RowHeightProperty, value);
    }

    private static bool IsRight(UIElement child) =>
        child is FrameworkElement { DataContext: Workspace { IsPersonal: true } };

    /// <summary>Столбцы одной стороны: по Rows ярлыков, ширина столбца — по самому широкому.</summary>
    private List<(List<UIElement> Items, double Width)> Columns(bool right)
    {
        var rows = Math.Max(1, Rows);
        var columns = new List<(List<UIElement>, double)>();
        var side = InternalChildren.Cast<UIElement>().Where(c => IsRight(c) == right).ToList();

        for (var i = 0; i < side.Count; i += rows)
        {
            var items = side.Skip(i).Take(rows).ToList();
            columns.Add((items, items.Max(c => c.DesiredSize.Width)));
        }

        return columns;
    }

    protected override Size MeasureOverride(Size available)
    {
        var rowHeight = double.IsNaN(RowHeight) ? available.Height : RowHeight;
        var tallest = 0.0;

        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, rowHeight));
            tallest = Math.Max(tallest, child.DesiredSize.Height);
        }

        var width = Columns(right: false).Sum(c => c.Width) + Columns(right: true).Sum(c => c.Width);
        var height = double.IsNaN(RowHeight) ? tallest : RowHeight * Math.Max(1, Rows);

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var tallest = InternalChildren.Cast<UIElement>().Select(c => c.DesiredSize.Height).DefaultIfEmpty(0).Max();
        var rowHeight = double.IsNaN(RowHeight) ? (Rows <= 1 ? final.Height : tallest) : RowHeight;

        var left = Columns(right: false);
        var right = Columns(right: true);

        var x = Place(left, 0, rowHeight);
        Place(right, Math.Max(x, final.Width - right.Sum(c => c.Width)), rowHeight);

        return final;
    }

    private static double Place(List<(List<UIElement> Items, double Width)> columns, double x, double rowHeight)
    {
        foreach (var (items, width) in columns)
        {
            for (var row = 0; row < items.Count; row++)
            {
                items[row].Arrange(new Rect(x, row * rowHeight, width, rowHeight));
            }

            x += width;
        }

        return x;
    }
}
