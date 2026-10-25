using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Controls;

namespace MultiServerSync.Core;

/// <summary>
/// Значок программы для шапки окна: <c>Icon="{core:AppIcon}"</c>. Обычный {ui:ImageIcon} с .ico
/// всегда берёт первый кадр (16 px) и растягивает его — на экране 150% значок мылится.
/// Здесь берётся кадр ровно под масштаб монитора, а при переезде окна на другой монитор — меняется.
/// </summary>
public sealed class AppIconExtension : MarkupExtension
{
    private static readonly Uri IcoUri = new("pack://application:,,,/Assets/app.ico");
    private static BitmapDecoder? _decoder;

    /// <summary>Размер в единицах WPF.</summary>
    public double Size { get; set; } = 16;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var icon = new ImageIcon { Width = Size, Height = Size, UseLayoutRounding = true };
        Window? window = null;

        void Apply() => icon.Source = Frame(Size * VisualTreeHelper.GetDpi(icon).DpiScaleX);
        void OnDpi(object sender, DpiChangedEventArgs e) => Apply();

        icon.Loaded += (_, _) =>
        {
            Apply();

            if (window is null && Window.GetWindow(icon) is { } w)
            {
                window = w;
                window.DpiChanged += OnDpi;
            }
        };

        return icon;
    }

    /// <summary>Самый маленький кадр не меньше нужного (уменьшать не растягивая), иначе самый крупный.</summary>
    public static BitmapSource Frame(double pixels)
    {
        _decoder ??= BitmapDecoder.Create(
            Application.GetResourceStream(IcoUri)!.Stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);

        var frames = _decoder.Frames.OrderBy(f => f.PixelWidth).ToList();
        return frames.FirstOrDefault(f => f.PixelWidth >= Math.Round(pixels)) ?? frames[^1];
    }
}
