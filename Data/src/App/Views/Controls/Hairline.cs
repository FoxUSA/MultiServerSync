using System.Windows;
using System.Windows.Media;

namespace MultiServerSync.Views.Controls;

/// <summary>
/// Вертикальная черта ровно в одну точку экрана у правого края (или левого, см. <see cref="AtLeft"/>). Обычная граница шириной 1 на 150% — это
/// полторы точки: смотря где столбец начался, она выходила то в одну, то в две (23.11, черты в шапке таблиц).
/// Здесь ширина — одна точка при любом масштабе, а направляющие прижимают её к сетке экрана.
/// </summary>
public sealed class Hairline : FrameworkElement
{
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(Hairline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Черта у левого края, а не у правого. В шапке таблицы так и надо: заголовок справа рисуется позже,
    /// и его полупрозрачная заливка на дробной границе ложилась поверх черты соседа — та выходила то бледной,
    /// то в две точки (23.11). Черта следующего заголовка рисуется последней и закрывает стык.
    /// </summary>
    public static readonly DependencyProperty AtLeftProperty = DependencyProperty.Register(
        nameof(AtLeft), typeof(bool), typeof(Hairline),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool AtLeft
    {
        get => (bool)GetValue(AtLeftProperty);
        set => SetValue(AtLeftProperty, value);
    }

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Fill is null || ActualHeight <= 0)
        {
            return;
        }

        var pixel = Core.DevicePixel.Of(this);
        // у левого края — по центру стыка: заголовки стоят с шагом в полпикселя, и на стыке то нахлёст двух
        // полупрозрачных заливок, то щель. Черта ровно на спорной точке закрывает её целиком
        var x = AtLeft ? -pixel / 2 : ActualWidth - pixel;

        var guides = new GuidelineSet();
        guides.GuidelinesX.Add(x);
        guides.GuidelinesX.Add(x + pixel);

        dc.PushGuidelineSet(guides);
        dc.DrawRectangle(Fill, null, new Rect(x, 0, pixel, ActualHeight));
        dc.Pop();
    }
}
