using System;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Animation;

namespace MultiServerSync.Views.Controls;

/// <summary>
/// Общее мигание «идёт работа»: одна анимация на всю программу, к ней цепляются рамки ярлыков, разделов, архива
/// и окна ответа сессии. Поэтому мигают в такт, когда бы ни начали. Тикает, только пока есть кому мигать.
/// </summary>
public static class BusyPulse
{
    public static readonly DependencyProperty OnProperty = DependencyProperty.RegisterAttached(
        "On", typeof(bool), typeof(BusyPulse), new PropertyMetadata(false, OnOnChanged));

    public static bool GetOn(DependencyObject element) => (bool)element.GetValue(OnProperty);

    public static void SetOn(DependencyObject element, bool value) => element.SetValue(OnProperty, value);

    private static readonly DependencyProperty CountedProperty = DependencyProperty.RegisterAttached(
        "Counted", typeof(bool), typeof(BusyPulse), new PropertyMetadata(false));

    // сам такт — непоказываемый элемент, у него и крутится прозрачность
    private static readonly FrameworkElement Clock = new();
    private static int _count;

    private static void OnOnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        element.Loaded -= OnLoadedChanged;
        element.Unloaded -= OnLoadedChanged;
        if ((bool)e.NewValue)
        {
            element.Loaded += OnLoadedChanged;
            element.Unloaded += OnLoadedChanged;
        }

        Update(element);
    }

    private static void OnLoadedChanged(object sender, RoutedEventArgs e) => Update((FrameworkElement)sender);

    private static void Update(FrameworkElement element)
    {
        var want = GetOn(element) && element.IsLoaded;
        if (want == (bool)element.GetValue(CountedProperty))
        {
            return;
        }

        element.SetValue(CountedProperty, want);
        if (want)
        {
            element.SetBinding(UIElement.OpacityProperty, new Binding(nameof(UIElement.Opacity)) { Source = Clock });
            if (++_count == 1)
            {
                Clock.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromSeconds(0.7))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                });
            }
        }
        else
        {
            BindingOperations.ClearBinding(element, UIElement.OpacityProperty);
            if (--_count == 0)
            {
                Clock.BeginAnimation(UIElement.OpacityProperty, null);
            }
        }
    }
}
