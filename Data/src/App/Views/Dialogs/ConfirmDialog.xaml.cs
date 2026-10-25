using System.Windows;
using Wpf.Ui.Controls;

namespace MultiServerSync.Views.Dialogs;

/// <summary>
/// Подтверждение действия в оформлении программы. Кнопка согласия называет само действие
/// («Удалить», «Закрыть набор»), а не безликое «Да»; фокус стоит на «Отмене»,
/// так что случайный Enter ничего не сносит. Esc — отмена.
/// </summary>
public partial class ConfirmDialog : FluentWindow
{
    private ConfirmDialog(string headline, string detail, string confirmText, string cancelText)
    {
        InitializeComponent();

        HeadlineText.Text = headline;
        DetailText.Text = detail;
        DetailText.Visibility = string.IsNullOrWhiteSpace(detail) ? Visibility.Collapsed : Visibility.Visible;
        ConfirmButton.Content = confirmText;
        CancelButton.Content = cancelText;

        Loaded += (_, _) => CancelButton.Focus();
    }

    /// <summary>Спрашивает и возвращает true, только если нажали кнопку действия.</summary>
    /// <param name="headline">Вопрос одной строкой — жирным.</param>
    /// <param name="detail">Что будет последствием. Пусто — строки нет.</param>
    /// <param name="confirmText">Подпись кнопки согласия: само действие.</param>
    /// <param name="cancelText">Подпись кнопки отказа.</param>
    public static bool Ask(Window? owner, string headline, string detail, string confirmText, string cancelText = "Отмена")
    {
        var dialog = new ConfirmDialog(headline, detail, confirmText, cancelText);

        if (owner is { IsLoaded: true })
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return dialog.ShowDialog() == true;
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
