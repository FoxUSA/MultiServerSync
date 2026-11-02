using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace MultiServerSync.Views.Controls;

/// <summary>
/// Окно команды — большое поле, а текста в нём мало. Само поле начинает выделение только с нажатия по
/// строкам текста: нажатие в пустоте под ними и в отступах под значки ничего не выделяло. Здесь — как в
/// редакторах кода: откуда бы ни повели мышь с зажатой кнопкой, выделение идёт от ближайшего места в тексте.
/// </summary>
public static class EmptyAreaSelect
{
    public static readonly DependencyProperty IsOnProperty = DependencyProperty.RegisterAttached(
        "IsOn",
        typeof(bool),
        typeof(EmptyAreaSelect),
        new PropertyMetadata(false, OnIsOnChanged));

    /// <summary>От какого места текста идёт выделение; нет значения — мышь ведёт само поле.</summary>
    private static readonly DependencyProperty AnchorProperty = DependencyProperty.RegisterAttached(
        "Anchor",
        typeof(int?),
        typeof(EmptyAreaSelect));

    public static bool GetIsOn(DependencyObject d) => (bool)d.GetValue(IsOnProperty);

    public static void SetIsOn(DependencyObject d, bool value) => d.SetValue(IsOnProperty, value);

    private static void OnIsOnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box)
        {
            return;
        }

        box.PreviewMouseLeftButtonDown -= OnDown;
        box.PreviewMouseMove -= OnMove;
        box.PreviewMouseLeftButtonUp -= OnUp;
        box.LostMouseCapture -= OnCaptureLost;

        if (e.NewValue is true)
        {
            box.PreviewMouseLeftButtonDown += OnDown;
            box.PreviewMouseMove += OnMove;
            box.PreviewMouseLeftButtonUp += OnUp;
            box.LostMouseCapture += OnCaptureLost;
        }
    }

    private static void OnDown(object sender, MouseButtonEventArgs e)
    {
        var box = (TextBox)sender;

        if (e.ClickCount > 1
            || Keyboard.Modifiers != ModifierKeys.None
            || !InEmptyArea(e.OriginalSource as DependencyObject, box))
        {
            return;
        }

        box.Focus();

        var anchor = IndexAt(box, e.GetPosition(box));

        box.Select(anchor, 0);
        box.SetValue(AnchorProperty, anchor);
        box.CaptureMouse();
        e.Handled = true;
    }

    private static void OnMove(object sender, MouseEventArgs e)
    {
        var box = (TextBox)sender;

        if (box.GetValue(AnchorProperty) is not int anchor)
        {
            return;
        }

        var point = e.GetPosition(box);
        var index = IndexAt(box, point);

        box.Select(Math.Min(anchor, index), Math.Abs(index - anchor));
        ScrollAtEdge(box, point);
        e.Handled = true;
    }

    private static void OnUp(object sender, MouseButtonEventArgs e)
    {
        var box = (TextBox)sender;

        if (box.GetValue(AnchorProperty) is int)
        {
            box.ClearValue(AnchorProperty);
            box.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private static void OnCaptureLost(object sender, MouseEventArgs e) => ((TextBox)sender).ClearValue(AnchorProperty);

    /// <summary>Строки текста, полосы прокрутки и то, что поверх поля, живут своей жизнью; остальное — пустота поля.</summary>
    private static bool InEmptyArea(DependencyObject? source, TextBox box)
    {
        for (var node = source; node is not null && !ReferenceEquals(node, box); node = VisualTreeHelper.GetParent(node))
        {
            if (node is ScrollBar or ButtonBase || node.GetType().Name == "TextBoxView")
            {
                return false;
            }
        }

        return source is not null;
    }

    /// <summary>Место между символами, ближайшее к точке: ниже текста — конец, правее строки — конец этой строки.</summary>
    private static int IndexAt(TextBox box, Point point)
    {
        var length = box.Text.Length;

        if (length == 0 || point.Y > box.GetRectFromCharacterIndex(length).Bottom)
        {
            return length;
        }

        var index = box.GetCharacterIndexFromPoint(point, snapToText: true);

        if (index < 0)
        {
            return length;
        }

        var line = box.GetLineIndexFromCharacterIndex(index);
        var start = box.GetCharacterIndexFromLineIndex(line);
        var end = start + box.GetLineText(line).TrimEnd('\r', '\n').Length;

        if (index >= end)
        {
            return end;
        }

        var left = box.GetRectFromCharacterIndex(index).Left;
        var right = box.GetRectFromCharacterIndex(index + 1).Left;

        return point.X > (left + right) / 2 ? index + 1 : index;
    }

    /// <summary>Мышь ушла за край поля — текст подъезжает, как при обычном выделении.</summary>
    private static void ScrollAtEdge(TextBox box, Point point)
    {
        if (point.Y < 0)
        {
            box.LineUp();
        }
        else if (point.Y > box.ActualHeight)
        {
            box.LineDown();
        }

        if (point.X < 0)
        {
            box.LineLeft();
        }
        else if (point.X > box.ActualWidth)
        {
            box.LineRight();
        }
    }
}
