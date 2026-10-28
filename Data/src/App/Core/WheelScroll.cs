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
///
/// Как в Firefox, колесо держится за тот список, который начал ехать, пока его крутят: долистали
/// длинную команду до конца — колесо стоит, а не срывается листать всю страницу; листаем страницу —
/// подъехавшая под курсор команда колесо не перехватывает. Отпускает пауза колеса или движение мыши.
/// </summary>
public static class WheelScroll
{
    /// <summary>Колесо молчит дольше этого — следующий щелчок ищет список заново.</summary>
    private const int LatchMs = 1000;

    /// <summary>Мышь сдвинули хотя бы на столько после этой паузы — тоже ищем заново.</summary>
    private const int MoveAfterMs = 100;
    private const double MoveDistance = 4;

    /// <summary>Список, который едет сейчас, когда колесо крутили в последний раз и где была мышь.</summary>
    private static ScrollViewer? _latched;
    private static int _lastWheel;
    private static Window? _wheelWindow;
    private static Point _wheelPoint;

    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnWheel));
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseMoveEvent, new MouseEventHandler(OnMove));
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0 || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        var latched = unchecked(e.Timestamp - _lastWheel) < LatchMs && _latched is { IsVisible: true } ? _latched : null;
        _lastWheel = e.Timestamp;
        _wheelWindow = sender as Window;
        _wheelPoint = e.GetPosition(_wheelWindow);

        var down = e.Delta < 0;

        // колесо ещё крутят — едет тот же список, а упёрся — просто стоит
        if (latched is not null)
        {
            Scroll(latched, down, e.Delta);
            e.Handled = true;
            return;
        }

        for (var node = e.OriginalSource as DependencyObject; node is not null; node = Parent(node))
        {
            if (node is ScrollViewer scroller && Scroll(scroller, down, e.Delta))
            {
                _latched = scroller;
                e.Handled = true;
                return;
            }
        }

        _latched = null;
    }

    private static void OnMove(object sender, MouseEventArgs e)
    {
        if (_latched is null
            || sender != _wheelWindow
            || unchecked(e.Timestamp - _lastWheel) < MoveAfterMs
            || (e.GetPosition(_wheelWindow) - _wheelPoint).Length < MoveDistance)
        {
            return;
        }

        _latched = null;
    }

    /// <summary>Сдвигает список, если ему есть куда; лента без вертикали едет вбок.</summary>
    private static bool Scroll(ScrollViewer scroller, bool down, int delta)
    {
        if (CanScrollVertically(scroller, down))
        {
            ScrollVertically(scroller, down, delta);
            return true;
        }

        if (scroller.ScrollableHeight <= 0 && CanScrollHorizontally(scroller, down))
        {
            scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset - delta);
            return true;
        }

        return false;
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
