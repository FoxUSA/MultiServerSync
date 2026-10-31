using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace MultiServerSync.Views.Controls;

/// <summary>
/// Под замком вкладки: кнопка тусклая, к её подсказке снизу добавлено, что требуется снять замок. Нажатие кнопка
/// пропускает к своему обработчику, он и говорит об отказе плашкой.
/// </summary>
public static class LockMute
{
    public const string Hint = "Требуется снять замок";

    public static readonly DependencyProperty IsOnProperty = DependencyProperty.RegisterAttached(
        "IsOn", typeof(bool), typeof(LockMute), new PropertyMetadata(false, OnChanged));

    private static readonly DependencyProperty OwnTipProperty = DependencyProperty.RegisterAttached(
        "OwnTip", typeof(object), typeof(LockMute));

    public static bool GetIsOn(DependencyObject element) => (bool)element.GetValue(IsOnProperty);

    public static void SetIsOn(DependencyObject element, bool value) => element.SetValue(IsOnProperty, value);

    private static void OnChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not FrameworkElement element)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            element.SetValue(OwnTipProperty, element.ToolTip);
            element.ToolTip = element.ToolTip is string { Length: > 0 } own ? own + "\n" + Hint : Hint;

            // поле и параметры отключает IsEnabled: у них бледнеет сам шаблон, второй раз гасить незачем
            if (element is ButtonBase && element.IsEnabled)
            {
                element.Opacity = 0.4;
            }

            ToolTipService.SetShowOnDisabled(element, true);
            return;
        }

        element.ToolTip = element.GetValue(OwnTipProperty);
        element.ClearValue(OwnTipProperty);
        element.ClearValue(UIElement.OpacityProperty);
        element.ClearValue(ToolTipService.ShowOnDisabledProperty);
    }
}
