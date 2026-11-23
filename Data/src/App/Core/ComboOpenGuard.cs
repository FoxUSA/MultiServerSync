using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MultiServerSync.Core;

/// <summary>
/// Список, открытый щелчком, не выбирает пункт на отпускании того же щелчка. У края экрана (нижняя
/// панель) список раскрывается поверх самого поля, под курсором оказывается пункт — отпустили кнопку,
/// он выбрался и список закрылся. Выбор протяжкой (нажал, повёл к пункту, отпустил) остаётся.
/// </summary>
public static class ComboOpenGuard
{
    /// <summary>Список, нажатие на котором его открывает; null — такого нажатия нет.</summary>
    private static ComboBox? _opening;

    /// <summary>Где нажали, в точках экрана.</summary>
    private static Point _pressAt;

    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(ComboBox), UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnPress), true);
        EventManager.RegisterClassHandler(typeof(ComboBox), UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(OnRelease), true);
    }

    private static void OnPress(object sender, MouseButtonEventArgs e)
    {
        var box = (ComboBox)sender;
        _opening = box.IsDropDownOpen ? null : box;

        if (_opening is not null)
        {
            _pressAt = box.PointToScreen(e.GetPosition(box));
        }
    }

    private static void OnRelease(object sender, MouseButtonEventArgs e)
    {
        var box = (ComboBox)sender;

        if (!ReferenceEquals(_opening, box))
        {
            return;
        }

        _opening = null;

        if (!box.IsDropDownOpen || !OnItem(e.OriginalSource as DependencyObject))
        {
            return;
        }

        var at = box.PointToScreen(e.GetPosition(box));
        var moved = Math.Abs(at.X - _pressAt.X) > SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(at.Y - _pressAt.Y) > SystemParameters.MinimumVerticalDragDistance;

        if (!moved)
        {
            // список остаётся открытым, пункт не выбран
            e.Handled = true;
        }
    }

    private static bool OnItem(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is ComboBoxItem)
            {
                return true;
            }

            if (node is ComboBox)
            {
                return false;
            }

            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return false;
    }
}
