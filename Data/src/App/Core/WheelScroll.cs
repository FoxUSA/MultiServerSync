using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MultiServerSync.Core;

/// <summary>
/// Колесо мыши во всех окнах программы — как в браузере и проводнике: прокручивается ближайший
/// к курсору список, который ещё может ехать в эту сторону; упёрся — едет внешний.
///
/// Своё нужно потому, что WPF отдаёт колесо самому внутреннему ScrollViewer, даже если тот
/// прокручиваться не может, а то и вовсе его теряет: список «Целевые сервера» внутри левой панели
/// колесом не листался. Ряд ярлыков (прокрутка только вбок) колесо листает вбок.
/// Окно под курсором активировать не нужно: Windows само отдаёт колесо окну под мышью.
/// </summary>
public static class WheelScroll
{
    public static void Register() =>
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnWheel));

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0 || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        for (var node = e.OriginalSource as DependencyObject; node is not null; node = Parent(node))
        {
            if (node is not ScrollViewer scroller)
            {
                continue;
            }

            var down = e.Delta < 0;

            if (CanScrollVertically(scroller, down))
            {
                ScrollVertically(scroller, down, e.Delta);
                e.Handled = true;
                return;
            }

            // лента без вертикали — колесо листает её вбок
            if (scroller.ScrollableHeight <= 0 && CanScrollHorizontally(scroller, down))
            {
                scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset - e.Delta);
                e.Handled = true;
                return;
            }
        }
    }

    private static bool CanScrollVertically(ScrollViewer scroller, bool down) =>
        down
            ? scroller.VerticalOffset < scroller.ScrollableHeight - 0.01
            : scroller.VerticalOffset > 0.01;

    private static bool CanScrollHorizontally(ScrollViewer scroller, bool right) =>
        right
            ? scroller.HorizontalOffset < scroller.ScrollableWidth - 0.01
            : scroller.HorizontalOffset > 0.01;

    /// <summary>
    /// Строками, как штатно: у таблиц с построчной прокруткой строка — это запись, у остального —
    /// 16 точек. Сколько строк за щелчок колеса — из настроек Windows.
    /// </summary>
    private static void ScrollVertically(ScrollViewer scroller, bool down, int delta)
    {
        var lines = SystemParameters.WheelScrollLines;

        // в Windows выбрано «на один экран»
        if (lines <= 0)
        {
            if (down)
            {
                scroller.PageDown();
            }
            else
            {
                scroller.PageUp();
            }

            return;
        }

        // тачпад шлёт мелкие доли щелчка — не меньше одной строки
        var count = Math.Max(1, (int)Math.Round(lines * Math.Abs(delta) / 120.0));

        for (var i = 0; i < count; i++)
        {
            if (down)
            {
                scroller.LineDown();
            }
            else
            {
                scroller.LineUp();
            }
        }
    }

    /// <summary>Наверх по дереву; из выпадающего списка — через его Popup к владельцу.</summary>
    private static DependencyObject? Parent(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);
}
