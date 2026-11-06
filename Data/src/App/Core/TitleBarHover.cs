using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace MultiServerSync.Core;

/// <summary>
/// Подсветка кнопок заголовка во всех окнах программы — по положению курсора. WPF UI ставит её по сообщениям
/// Windows о мыши над рамкой окна, а они теряются: курсор стоит на крестике, красного нет, пока не поводишь мышью.
/// Пока окно открыто, курсор сверяется с кнопками каждые 40 мс. Нажатие кнопок по-прежнему делает WPF UI.
/// </summary>
public static class TitleBarHover
{
    private const uint RootAncestor = 2;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint flags);

    private struct POINT
    {
        public int X;
        public int Y;
    }

    public static void Register() =>
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window || !ReferenceEquals(e.OriginalSource, window))
        {
            return;
        }

        var buttons = Find<TitleBarButton>(window).ToList();

        if (buttons.Count == 0)
        {
            return;
        }

        var timer = new DispatcherTimer(DispatcherPriority.Input, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) => Track(window, buttons);
        timer.Start();
        window.Closed += (_, _) => timer.Stop();
    }

    private static void Track(Window window, List<TitleBarButton> buttons)
    {
        var handle = new WindowInteropHelper(window).Handle;

        if (handle == 0 || !GetCursorPos(out var cursor))
        {
            return;
        }

        // под курсором — это окно, а не то, что его перекрыло
        var mine = GetAncestor(WindowFromPoint(cursor), RootAncestor) == handle;

        foreach (var button in buttons)
        {
            var over = mine && button.IsVisible && PresentationSource.FromVisual(button) is not null
                && new Rect(button.RenderSize).Contains(button.PointFromScreen(new Point(cursor.X, cursor.Y)));

            if (over)
            {
                button.Hover();
            }
            else if (button.IsHovered)
            {
                button.RemoveHover();
            }
        }
    }

    private static IEnumerable<T> Find<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is T match)
            {
                yield return match;
            }

            foreach (var deeper in Find<T>(child))
            {
                yield return deeper;
            }
        }
    }
}
