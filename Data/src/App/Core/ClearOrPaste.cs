using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using MultiServerSync.Services;
using Wpf.Ui.Controls;

namespace MultiServerSync.Core;

/// <summary>
/// В полях с крестиком очистки (ui:TextBox) пустое поле в фокусе показывает на месте крестика
/// значок «Вставить»: очистили — и тут же вставили своё. Кнопка та же, что у крестика, из шаблона
/// WPF UI: её видимость и значок ставятся своими значениями, они сильнее триггеров шаблона.
/// Поле пароля — тоже: вставка идёт как ввод, Password и звёздочки ставит само поле.
/// Поля с ClearButtonEnabled="False" и NumberBox не трогаются.
/// </summary>
public static class ClearOrPaste
{
    private static readonly ConditionalWeakTable<Wpf.Ui.Controls.TextBox, ButtonBase> Hooked = new();

    public static void Register() =>
        EventManager.RegisterClassHandler(
            typeof(Wpf.Ui.Controls.TextBox), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.TextBox box
            || box is NumberBox
            || !box.ClearButtonEnabled
            || Hooked.TryGetValue(box, out _))
        {
            return;
        }

        box.ApplyTemplate();

        if (box.Template?.FindName("ClearButton", box) is not ButtonBase button)
        {
            return;
        }

        Hooked.Add(box, button);
        button.Click += (_, _) => OnClick(box);
        button.MouseEnter += (_, _) => Update(box, button);
        box.TextChanged += (_, _) => Update(box, button);
        box.IsKeyboardFocusWithinChanged += (_, _) => Update(box, button);
        box.IsEnabledChanged += (_, _) => Update(box, button);
        Update(box, button);
    }

    /// <summary>Пустое поле, куда можно писать, — значок вставки.</summary>
    private static bool PasteMode(Wpf.Ui.Controls.TextBox box) =>
        box.Text.Length == 0 && !box.IsReadOnly && box.IsEnabled;

    private static void Update(Wpf.Ui.Controls.TextBox box, ButtonBase button)
    {
        var focused = box.IsKeyboardFocusWithin;

        if (!box.ClearButtonEnabled || !focused || (box.Text.Length == 0 && !PasteMode(box)))
        {
            button.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            return;
        }

        button.SetValue(UIElement.VisibilityProperty, Visibility.Visible);
        button.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 4, 0));

        if (button is not Wpf.Ui.Controls.Button ui)
        {
            return;
        }

        if (box.Text.Length > 0)
        {
            ui.ClearValue(Wpf.Ui.Controls.Button.IconProperty);
            ui.ClearValue(FrameworkElement.ToolTipProperty);
            ui.ClearValue(UIElement.OpacityProperty);
            return;
        }

        ui.Icon = new SymbolIcon { Symbol = SymbolRegular.ClipboardPaste24, FontSize = box.FontSize };
        ui.ToolTip = "Вставить из буфера обмена";

        // буфер без текста — значок тусклый, но жмётся: нажатие объяснит, почему ничего не вставилось
        ui.Opacity = ClipboardText.HasText ? 1.0 : 0.4;
    }

    private static void OnClick(Wpf.Ui.Controls.TextBox box)
    {
        if (!PasteMode(box))
        {
            // крестик: очищает сама кнопка шаблона
            return;
        }

        // команда кнопки шаблона идёт после Click и очистила бы вставленное — вставка после неё
        box.Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Paste(box));
    }

    private static void Paste(Wpf.Ui.Controls.TextBox box)
    {
        string text;

        try
        {
            text = ClipboardText.ReadText();
        }
        catch (Exception ex)
        {
            AppLog.Hint("Не удалось прочитать буфер обмена: " + ex.Message, LogLevel.Warning);
            return;
        }

        if (text.Length == 0)
        {
            AppLog.Hint("В буфере обмена нет текста — вставлять нечего.", LogLevel.Warning);
            return;
        }

        box.Focus();

        // как Ctrl+V: свои обработчики вставки у поля (адрес:порт у сервера) срабатывают и тут
        var args = new DataObjectPastingEventArgs(new DataObject(DataFormats.UnicodeText, text), false, DataFormats.UnicodeText)
        {
            RoutedEvent = DataObject.PastingEvent,
        };
        box.RaiseEvent(args);

        if (args.CommandCancelled)
        {
            return;
        }

        if (args.DataObject.GetData(DataFormats.UnicodeText) is string pasted)
        {
            text = pasted;
        }

        if (box is Wpf.Ui.Controls.PasswordBox)
        {
            // пароль: первая строка как есть — пробелы могут быть его частью; звёздочки поставит само поле
            text = text.Split('\n')[0].TrimEnd('\r');
        }
        else if (!box.AcceptsReturn)
        {
            // однострочное поле: первая непустая строка, без пробелов по краям
            text = text.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? string.Empty;
        }

        if (box.MaxLength > 0 && text.Length > box.MaxLength)
        {
            text = text[..box.MaxLength];
        }

        if (text.Length == 0)
        {
            AppLog.Hint("В буфере обмена нет текста — вставлять нечего.", LogLevel.Warning);
            return;
        }

        box.SelectedText = text;
        box.CaretIndex = box.Text.Length;
    }
}
