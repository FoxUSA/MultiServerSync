using System.Windows;
using System.Windows.Controls;

namespace MultiServerSync.Views.Controls;

/// <summary>
/// Лента вкладок: ярлыки одного размера идут в ряд, не влезли по ширине — переносятся
/// в следующий ряд, и лента растёт вниз.
/// </summary>
public sealed class SplitTabsPanel : Panel
{
    public static readonly DependencyProperty RowHeightProperty = DependencyProperty.Register(
        nameof(RowHeight),
        typeof(double),
        typeof(SplitTabsPanel),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Высота ряда; не задана — по самому высокому ярлыку.</summary>
    public double RowHeight
    {
        get => (double)GetValue(RowHeightProperty);
        set => SetValue(RowHeightProperty, value);
    }

    /// <summary>Сколько мест в ряду при такой ширине; бесконечная ширина — все в один ряд.</summary>
    private int PerRow(double width, double slot) =>
        double.IsInfinity(width) || slot <= 0
            ? Math.Max(1, InternalChildren.Count)
            : Math.Max(1, (int)Math.Floor(width / slot));

    private int RowsFor(int perRow) => Math.Max(1, (InternalChildren.Count + perRow - 1) / perRow);

    protected override Size MeasureOverride(Size available)
    {
        var tallest = 0.0;
        var widest = 0.0;

        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, double.IsNaN(RowHeight) ? double.PositiveInfinity : RowHeight));
            tallest = Math.Max(tallest, child.DesiredSize.Height);
            widest = Math.Max(widest, child.DesiredSize.Width);
        }

        var rowHeight = double.IsNaN(RowHeight) ? tallest : RowHeight;
        var perRow = PerRow(available.Width, widest);
        var rows = InternalChildren.Count == 0 ? 1 : RowsFor(perRow);
        var width = double.IsInfinity(available.Width) ? widest * InternalChildren.Count : available.Width;

        return new Size(width, rowHeight * rows);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var children = InternalChildren.Cast<UIElement>().ToList();
        var widest = children.Select(c => c.DesiredSize.Width).DefaultIfEmpty(0).Max();
        var tallest = children.Select(c => c.DesiredSize.Height).DefaultIfEmpty(0).Max();
        var rowHeight = double.IsNaN(RowHeight) ? tallest : RowHeight;
        var perRow = PerRow(final.Width, widest);

        for (var i = 0; i < children.Count; i++)
        {
            children[i].Arrange(new Rect(i % perRow * widest, i / perRow * rowHeight, widest, rowHeight));
        }

        return final;
    }
}
