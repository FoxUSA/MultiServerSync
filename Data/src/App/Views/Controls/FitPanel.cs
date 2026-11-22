using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace MultiServerSync.Views.Controls;

/// <summary>
/// Куски текста в одну строку: кусок либо виден целиком, либо убран целиком — ни многоточий, ни срезанных букв.
/// Не влезли — уходят с конца, поэтому куски ставятся по убыванию важности. Между видимыми — зазор
/// и, если задан, разделитель («·»), он рисуется самой панелью. Высота строки — по самому высокому куску,
/// видим он или нет: строка не прыгает, когда куски уходят. Так в макете «Мониторинга» сделан приём с переносом.
/// </summary>
public sealed class FitPanel : Panel
{
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(FitPanel),
        new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SeparatorProperty = DependencyProperty.Register(
        nameof(Separator), typeof(string), typeof(FitPanel),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SeparatorBrushProperty = DependencyProperty.Register(
        nameof(SeparatorBrush), typeof(Brush), typeof(FitPanel),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AlignProperty = DependencyProperty.Register(
        nameof(Align), typeof(HorizontalAlignment), typeof(FitPanel),
        new FrameworkPropertyMetadata(HorizontalAlignment.Left, FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty BaselineProperty = DependencyProperty.Register(
        nameof(Baseline), typeof(bool), typeof(FitPanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsArrange));

    /// <summary>Сколько кусков с начала видно после последней раскладки.</summary>
    public int Shown { get; private set; }

    /// <summary>Зазор между кусками (с разделителем — с каждой его стороны).</summary>
    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    /// <summary>Знак между видимыми кусками; null — только зазор.</summary>
    public string? Separator
    {
        get => (string?)GetValue(SeparatorProperty);
        set => SetValue(SeparatorProperty, value);
    }

    public Brush SeparatorBrush
    {
        get => (Brush)GetValue(SeparatorBrushProperty);
        set => SetValue(SeparatorBrushProperty, value);
    }

    /// <summary>Куда прижаты видимые куски: влево, вправо, к середине; Stretch — первый слева, последний справа, лишнее место между ними.</summary>
    public HorizontalAlignment Align
    {
        get => (HorizontalAlignment)GetValue(AlignProperty);
        set => SetValue(AlignProperty, value);
    }

    /// <summary>Тексты разного кегля — по одной линии букв, как в макете у крупного значения и подписи к нему.</summary>
    public bool Baseline
    {
        get => (bool)GetValue(BaselineProperty);
        set => SetValue(BaselineProperty, value);
    }

    public FitPanel()
    {
        // убранные куски кладутся за левый край — их не видно и мышь их не ловит
        ClipToBounds = true;
    }

    /// <summary>Ширина всех кусков без ужатия — сколько строке нужно, чтобы показать всё.</summary>
    public double FullWidth { get; private set; }

    /// <summary>Ширина первого куска — меньше него строка не бывает полезной.</summary>
    public double FirstWidth { get; private set; }

    private readonly List<double> _separatorsAt = new();

    private FormattedText? Mark()
    {
        if (Separator is not { Length: > 0 } text)
        {
            return null;
        }

        var size = GetValue(TextElement.FontSizeProperty) is double s ? s : 12;
        var family = GetValue(TextElement.FontFamilyProperty) as FontFamily ?? new FontFamily("Segoe UI");

        return new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size,
            SeparatorBrush,
            null,
            TextFormattingMode.Display,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    private double Spacer(FormattedText? mark) => mark is null ? Gap : Gap * 2 + mark.WidthIncludingTrailingWhitespace;

    protected override Size MeasureOverride(Size available)
    {
        var mark = Mark();
        var spacer = Spacer(mark);
        double x = 0, full = 0, height = 0;
        var shown = 0;
        var stopped = false;
        var first = true;

        // запас в точку экрана: на 125–250% ширины кусков округляются, «ровно впритык» не должно терять кусок
        var slack = Core.DevicePixel.Of(this);

        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            child.Measure(new Size(double.PositiveInfinity, available.Height));
            var w = child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);

            var extra = first ? 0 : spacer;
            full += extra + w;

            if (first)
            {
                FirstWidth = w;
            }

            first = false;

            if (!stopped && x + extra + w <= available.Width + slack)
            {
                x += extra + w;
                shown++;
            }
            else
            {
                stopped = true;
            }
        }

        FullWidth = full;
        Shown = shown;

        if (mark is not null)
        {
            height = Math.Max(height, mark.Height);
        }

        return new Size(double.IsInfinity(available.Width) ? full : x, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var mark = Mark();
        var spacer = Spacer(mark);
        var visible = new List<UIElement>();
        double used = 0;

        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            if (visible.Count < Shown)
            {
                used += (visible.Count > 0 ? spacer : 0) + child.DesiredSize.Width;
                visible.Add(child);
            }
            else
            {
                child.Arrange(new Rect(-100000, 0, child.DesiredSize.Width, child.DesiredSize.Height));
            }
        }

        var x = Align switch
        {
            HorizontalAlignment.Right => Math.Max(0, finalSize.Width - used),
            HorizontalAlignment.Center => Math.Max(0, (finalSize.Width - used) / 2),
            _ => 0,
        };

        // линия букв: у текста она на высоте «baseline» шрифта; самый крупный задаёт её для всех
        var baseline = Baseline ? visible.Select(BaselineOf).DefaultIfEmpty(0).Max() : 0;

        _separatorsAt.Clear();

        // «Stretch»: как space-between в макете — свободное место поровну между кусками
        var spread = Align == HorizontalAlignment.Stretch && visible.Count > 1
            ? Math.Max(0, finalSize.Width - used) / (visible.Count - 1)
            : 0;

        for (var i = 0; i < visible.Count; i++)
        {
            var child = visible[i];

            if (i > 0)
            {
                if (mark is not null)
                {
                    _separatorsAt.Add(x + Gap + spread / 2);
                }

                x += spacer + spread;
            }

            var h = child.DesiredSize.Height;
            var y = Baseline && BaselineOf(child) is var own && own > 0
                ? baseline - own
                : (finalSize.Height - h) / 2;

            child.Arrange(new Rect(x, Math.Max(0, y), child.DesiredSize.Width, h));
            x += child.DesiredSize.Width;
        }

        InvalidateVisual();
        return finalSize;
    }

    private static double BaselineOf(UIElement element) => element switch
    {
        TextBlock text => text.FontFamily.Baseline * text.FontSize + text.Padding.Top + text.Margin.Top,
        _ => 0,
    };

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        if (Mark() is not { } mark)
        {
            return;
        }

        var y = (RenderSize.Height - mark.Height) / 2;

        foreach (var x in _separatorsAt)
        {
            dc.DrawText(mark, new Point(x, y));
        }
    }
}
