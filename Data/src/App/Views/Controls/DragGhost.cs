using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;

namespace MultiServerSync.Views.Controls;

/// <summary>
/// Что тащим мышью — у курсора, как в проводнике Windows: значок, имя или число, ниже — куда упадёт.
/// Окошко прозрачно для мыши: перетаскивание видит то, что под ним.
/// </summary>
internal sealed class DragGhost
{
    private readonly Popup _popup;
    private readonly TextBlock _action;
    private readonly FrameworkElement _owner;

    public DragGhost(FrameworkElement owner, ImageSource? icon, string title, int count)
    {
        _owner = owner;

        var name = new TextBlock
        {
            Text = title,
            MaxWidth = 320,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new Image { Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) });
        head.Children.Add(name);

        // число — кружком, как у проводника при нескольких файлах
        if (count > 1)
        {
            var badge = new Border
            {
                MinWidth = 18,
                Height = 18,
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(5, 0, 5, 0),
                CornerRadius = new CornerRadius(9),
                Child = new TextBlock
                {
                    Text = count.ToString(),
                    FontSize = 11,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            badge.SetResourceReference(Border.BackgroundProperty, "AccentFillColorDefaultBrush");
            head.Children.Add(badge);
        }

        _action = new TextBlock
        {
            Margin = new Thickness(24, 2, 0, 0),
            FontSize = 11,
            MaxWidth = 320,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Visibility = Visibility.Collapsed,
        };
        _action.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        var body = new StackPanel();
        body.Children.Add(head);
        body.Children.Add(_action);

        var frame = new Border
        {
            Padding = new Thickness(8, 5, 10, 6),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Opacity = 0.95,
            Child = body,
        };
        frame.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        frame.SetResourceReference(Border.BorderBrushProperty, "AccentFillColorDefaultBrush");
        TextOptions.SetTextFormattingMode(frame, TextFormattingMode.Display);

        _popup = new Popup
        {
            AllowsTransparency = true,
            Placement = PlacementMode.AbsolutePoint,
            PlacementTarget = owner,
            IsHitTestVisible = false,
            Focusable = false,
            Child = frame,
        };

        _popup.Opened += (_, _) => PassMouseThrough();
        Follow();
        _popup.IsOpen = true;
    }

    /// <summary>Куда упадёт: «Копировать в /var/www». Пусто — бросить тут нельзя.</summary>
    public void SetAction(string? text)
    {
        _action.Text = text ?? string.Empty;
        _action.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Правее и ниже курсора — чтобы не закрывать строку, на которую бросают.</summary>
    public void Follow()
    {
        if (!GetCursorPos(out var at) || PresentationSource.FromVisual(_owner)?.CompositionTarget is not { } target)
        {
            return;
        }

        var point = target.TransformFromDevice.Transform(new Point(at.X, at.Y));
        _popup.HorizontalOffset = point.X + 16;
        _popup.VerticalOffset = point.Y + 18;
    }

    public void Close() => _popup.IsOpen = false;

    private void PassMouseThrough()
    {
        if (PresentationSource.FromVisual(_popup.Child) is HwndSource source)
        {
            var style = GetWindowLong(source.Handle, GwlExStyle);
            SetWindowLong(source.Handle, GwlExStyle, style | WsExTransparent | WsExNoActivate);
        }
    }

    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExNoActivate = 0x08000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
