using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace MultiServerSync.Views.Controls;

/// <summary>
/// «Сохранить» без изменений — тусклая: сохранять нечего. Нажатие окно отказывает плашкой <see cref="Nothing"/>.
/// Не гасится совсем: погашенная кнопка WPF UI после включения не всегда перекрашивает текст.
/// Правки окна ловятся одним местом — любой ввод, выбор в списке, галочка где угодно в окне.
/// </summary>
public static class SaveGate
{
    public const string Nothing = "Изменений нет — сохранять нечего.";

    /// <summary>Следить за правками в <paramref name="root"/>: после каждой кнопка тусклая или нет по <paramref name="changed"/>.</summary>
    public static void Watch(FrameworkElement root, FrameworkElement button, Func<bool> changed)
    {
        // следом, а не сразу: привязка поля к данным обновляется уже после события
        void Update(object sender, RoutedEventArgs e) =>
            root.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() => Show(button, changed())));

        root.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(Update), handledEventsToo: true);
        root.AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler(Update), handledEventsToo: true);
        root.AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(Update), handledEventsToo: true);
        root.AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(Update), handledEventsToo: true);
        root.AddHandler(PasswordBox.PasswordChangedEvent, new RoutedEventHandler(Update), handledEventsToo: true);
        Show(button, changed());
    }

    /// <summary>Есть что сохранить — кнопка как обычно, нет — тусклая с подсказкой.</summary>
    public static void Show(FrameworkElement button, bool changed)
    {
        button.Opacity = changed ? 1 : 0.45;
        button.ToolTip = changed ? null : "Изменений нет";
        ToolTipService.SetShowOnDisabled(button, true);
    }

    /// <summary>Кнопка в координатах окна — плашка отказа встаёт под ней.</summary>
    public static Rect Anchor(FrameworkElement button, Visual host) =>
        button.TransformToVisual(host).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
}
