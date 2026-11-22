using System.Windows.Media;

namespace MultiServerSync.Core;

/// <summary>
/// Точка экрана в единицах WPF. На 125%, 175%, 225%, 250% Windows округляет поля и ширины до целых точек экрана,
/// и сумма кусков выходит на доли точки шире расчёта. Проверки «влезает ли» и наименьшие размеры окон берут
/// отсюда запас, а не сравнивают впритык. Масштаб — монитора, на котором элемент, а не главного.
/// </summary>
public static class DevicePixel
{
    /// <summary>Одна точка экрана: 1 на 100%, 0.8 на 125%, 0.4 на 250%.</summary>
    public static double Of(Visual visual)
    {
        var scale = VisualTreeHelper.GetDpi(visual).DpiScaleX;
        return scale > 0 ? 1 / scale : 1;
    }

    /// <summary>Наименьший размер окна: вверх до целых точек экрана и ещё точка запаса на округление полей.</summary>
    public static double Up(double size, Visual visual)
    {
        var pixel = Of(visual);
        return Math.Ceiling(size / pixel - 0.001) * pixel + pixel;
    }
}
