using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace MultiServerSync.Views.Controls;

/// <summary>
/// Поле пути: первый щелчок выделяет весь текст — набранное заменит старый путь; второй щелчок ставит курсор.
/// Так же ведёт себя адрес в «Проводнике». Работает на TextBox и на редактируемом ComboBox (его поле ввода).
/// </summary>
public static class SelectAllOnClick
{
    public static readonly DependencyProperty IsOnProperty = DependencyProperty.RegisterAttached(
        "IsOn", typeof(bool), typeof(SelectAllOnClick), new PropertyMetadata(false, OnChanged));

    public static bool GetIsOn(DependencyObject d) => (bool)d.GetValue(IsOnProperty);

    public static void SetIsOn(DependencyObject d, bool value) => d.SetValue(IsOnProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true)
        {
            return;
        }

        switch (d)
        {
            case TextBoxBase box:
                Hook(box);
                break;

            // поле ввода появляется с шаблоном
            case ComboBox combo:
                combo.Loaded += (_, _) =>
                {
                    combo.ApplyTemplate();

                    if (combo.Template?.FindName("PART_EditableTextBox", combo) is TextBox inner && !GetIsOn(inner))
                    {
                        SetIsOn(inner, true);
                    }
                };
                break;
        }
    }

    private static void Hook(TextBoxBase box)
    {
        box.PreviewMouseLeftButtonDown += (_, e) =>
        {
            // поле уже в работе — щелчок ставит курсор, как обычно
            if (box.IsKeyboardFocusWithin)
            {
                return;
            }

            e.Handled = true;
            box.Focus();
        };

        box.GotKeyboardFocus += (_, _) => box.SelectAll();
    }
}
