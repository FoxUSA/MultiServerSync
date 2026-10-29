using System.Windows;
using Wpf.Ui.Controls;

namespace MultiServerSync.Views.Dialogs;

/// <summary>
/// Подтверждение действия в оформлении программы. Кнопка согласия называет само действие
/// («Удалить», «Закрыть набор»), а не безликое «Да»; фокус стоит на «Отмене»,
/// так что случайный Enter ничего не сносит. Удаление и замену, о которых только что
/// попросили сами, Enter подтверждает (enterConfirms). Esc — отмена всегда.
/// </summary>
public partial class ConfirmDialog : FluentWindow
{
    private int _choice;

    private ConfirmDialog(string headline, string detail, string confirmText, string? extraText, string cancelText, bool enterConfirms)
    {
        InitializeComponent();

        HeadlineText.Text = headline;
        DetailText.Text = detail;
        DetailText.Visibility = string.IsNullOrWhiteSpace(detail) ? Visibility.Collapsed : Visibility.Visible;
        ConfirmButton.Content = confirmText;
        CancelButton.Content = cancelText;

        if (!string.IsNullOrEmpty(extraText))
        {
            ExtraButton.Content = extraText;
            ExtraButton.Visibility = Visibility.Visible;
        }

        if (enterConfirms)
        {
            ConfirmButton.IsDefault = true;
            Loaded += (_, _) => ConfirmButton.Focus();
        }
        else
        {
            Loaded += (_, _) => CancelButton.Focus();
        }
    }

    /// <summary>Спрашивает и возвращает true, только если нажали кнопку действия.</summary>
    /// <param name="headline">Вопрос одной строкой — жирным.</param>
    /// <param name="detail">Что будет последствием. Пусто — строки нет.</param>
    /// <param name="confirmText">Подпись кнопки согласия: само действие.</param>
    /// <param name="cancelText">Подпись кнопки отказа.</param>
    /// <param name="enterConfirms">Enter нажимает кнопку действия: удаление и замена, о которых только что попросили.</param>
    public static bool Ask(Window? owner, string headline, string detail, string confirmText, string cancelText = "Отмена", bool enterConfirms = false) =>
        Choose(owner, headline, detail, confirmText, null, null, out _, cancelText, enterConfirms) == 1;

    /// <summary>
    /// То же с третьей кнопкой между согласием и отказом. 0 — отказ (и Esc, и крестик),
    /// 1 — кнопка действия, 2 — третья кнопка.
    /// </summary>
    public static int Choose(Window? owner, string headline, string detail, string confirmText, string? extraText, string cancelText = "Отмена", bool enterConfirms = false) =>
        Choose(owner, headline, detail, confirmText, extraText, null, out _, cancelText, enterConfirms);

    /// <summary>То же с галочкой слева от кнопок — например, «больше не спрашивать».</summary>
    /// <param name="optionText">Подпись галочки; пусто — галочки нет.</param>
    /// <param name="option">Стояла ли галочка, когда окно закрыли.</param>
    public static int Choose(
        Window? owner,
        string headline,
        string detail,
        string confirmText,
        string? extraText,
        string? optionText,
        out bool option,
        string cancelText = "Отмена",
        bool enterConfirms = false)
    {
        var dialog = new ConfirmDialog(headline, detail, confirmText, extraText, cancelText, enterConfirms);

        if (!string.IsNullOrEmpty(optionText))
        {
            dialog.OptionBox.Content = optionText;
            dialog.OptionBox.Visibility = Visibility.Visible;
        }

        if (owner is { IsLoaded: true })
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dialog.ShowDialog();
        option = dialog.OptionBox.IsChecked == true;
        return dialog._choice;
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        _choice = 1;
        DialogResult = true;
    }

    private void OnExtra(object sender, RoutedEventArgs e)
    {
        _choice = 2;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _choice = 0;
        DialogResult = false;
    }
}
