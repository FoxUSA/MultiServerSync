using System.Windows;
using System.Windows.Controls;
using MultiServerSync.Services;

namespace MultiServerSync.Views.Controls;

/// <summary>
/// Лента вкладок в ряд: обычные — от левого края, личные вкладки команд — прижаты к правому.
/// Пока места хватает, между ними пусто; не хватает — встают вплотную и лента листается.
/// </summary>
public sealed class SplitTabsPanel : Panel
{
    private static bool IsRight(UIElement child) =>
        child is FrameworkElement { DataContext: Workspace { IsPersonal: true } };

    protected override Size MeasureOverride(Size available)
    {
        var width = 0.0;
        var height = 0.0;

        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, available.Height));
            width += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var x = 0.0;
        var right = 0.0;

        foreach (UIElement child in InternalChildren)
        {
            if (IsRight(child))
            {
                right += child.DesiredSize.Width;
                continue;
            }

            child.Arrange(new Rect(x, 0, child.DesiredSize.Width, final.Height));
            x += child.DesiredSize.Width;
        }

        x = Math.Max(x, final.Width - right);

        foreach (UIElement child in InternalChildren)
        {
            if (IsRight(child))
            {
                child.Arrange(new Rect(x, 0, child.DesiredSize.Width, final.Height));
                x += child.DesiredSize.Width;
            }
        }

        return final;
    }
}
