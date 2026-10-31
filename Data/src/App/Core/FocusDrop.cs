using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace MultiServerSync.Core;

/// <summary>
/// Щелчок по пустому месту снимает курсор с поля ввода, во всех окнах. Сам WPF фокус отдаёт только
/// тому, что умеет его брать, а панель и пустая таблица не умеют: курсор оставался в поле, и набранное
/// уходило туда. Щелчок по элементу управления поле не трогает: команды вставляют текст в поле под курсором.
/// </summary>
public static class FocusDrop
{
    public static void Register() =>
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler(OnPress));

    /// <summary>Щелчок в другом окне страницы: курсор из поля прежнего окна уходит, даже если нажата кнопка.</summary>
    public static void Leave(object window)
    {
        if (window is UIElement { IsKeyboardFocusWithin: false } && InField)
        {
            Keyboard.ClearFocus();
        }
    }

    private static bool InField => Keyboard.FocusedElement is TextBoxBase or PasswordBox;

    private static void OnPress(object sender, MouseButtonEventArgs e)
    {
        if (InField && !OnControl(e.OriginalSource as DependencyObject))
        {
            Keyboard.ClearFocus();
        }
    }

    private static bool OnControl(DependencyObject? node)
    {
        while (node is not null)
        {
            // Thumb — полосы прокрутки и разделители; строки сами берут фокус при щелчке
            if (node is ButtonBase or TextBoxBase or PasswordBox or ComboBox or Thumb or ScrollBar or ListBoxItem or DataGridRow or MenuItem)
            {
                return true;
            }

            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return false;
    }
}
