using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MultiServerSync.Views.Controls;

/// <summary>
/// Название на ярлыке: не обрезается никогда. Переносится по словам не больше чем в
/// <see cref="MaxLines"/> строк, а не влезло и так или слово длиннее ширины — шрифт
/// уменьшается, пока не влезет (но не мельче <see cref="MinFontSize"/>).
/// Обёртка, а не наследник TextBlock: у того замер закрыт для переопределения.
/// </summary>
public sealed class FitTextBlock : Decorator
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(FitTextBlock),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure,
            (d, e) => ((FitTextBlock)d)._text.Text = e.NewValue as string ?? string.Empty));

    public static readonly DependencyProperty MaxFontSizeProperty = DependencyProperty.Register(
        nameof(MaxFontSize), typeof(double), typeof(FitTextBlock),
        new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MinFontSizeProperty = DependencyProperty.Register(
        nameof(MinFontSize), typeof(double), typeof(FitTextBlock),
        new FrameworkPropertyMetadata(7.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxLinesProperty = DependencyProperty.Register(
        nameof(MaxLines), typeof(int), typeof(FitTextBlock),
        new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private readonly TextBlock _text = new()
    {
        FontWeight = FontWeights.SemiBold,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    public FitTextBlock()
    {
        Child = _text;
    }

    /// <summary>Жирность надписи. По умолчанию — как на ярлыках.</summary>
    public FontWeight TextWeight
    {
        get => _text.FontWeight;
        set
        {
            _text.FontWeight = value;
            InvalidateMeasure();
        }
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public double MaxFontSize
    {
        get => (double)GetValue(MaxFontSizeProperty);
        set => SetValue(MaxFontSizeProperty, value);
    }

    public double MinFontSize
    {
        get => (double)GetValue(MinFontSizeProperty);
        set => SetValue(MinFontSizeProperty, value);
    }

    public int MaxLines
    {
        get => (int)GetValue(MaxLinesProperty);
        set => SetValue(MaxLinesProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        // точка запаса на округление до пикселей
        _text.FontSize = double.IsInfinity(constraint.Width) || constraint.Width <= 1
            ? MaxFontSize
            : Fit(constraint.Width - 1);

        _text.Measure(constraint);
        return _text.DesiredSize;
    }

    /// <summary>Самый крупный шрифт с шагом 0.5, при котором каждое слово влезает в ширину, а строк не больше MaxLines.</summary>
    private double Fit(double width)
    {
        var text = Text ?? string.Empty;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var typeface = new Typeface(_text.FontFamily, _text.FontStyle, _text.FontWeight, _text.FontStretch);
        var dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // ярлыки рисуются в режиме Display, а он шире Ideal — мерить надо так же
        var mode = TextOptions.GetTextFormattingMode(this);

        FormattedText Format(string s, double size) =>
            new(s, CultureInfo.CurrentCulture, FlowDirection, typeface, size, Brushes.Black, null, mode, dip);

        for (var size = MaxFontSize; size > MinFontSize; size -= 0.5)
        {
            if (words.Any(w => Format(w, size).WidthIncludingTrailingWhitespace > width))
            {
                continue;
            }

            var all = Format(text, size);
            all.MaxTextWidth = width;

            if (all.Height <= Format("Ag", size).Height * MaxLines + 0.5)
            {
                return size;
            }
        }

        return MinFontSize;
    }
}
