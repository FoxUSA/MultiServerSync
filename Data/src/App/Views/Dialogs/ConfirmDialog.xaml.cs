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
    private int _choice;

    private ConfirmDialog(string headline, string detail, string confirmText, string? extraText, string cancelText)
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

        Loaded += (_, _) => CancelButton.Focus();
    }

    /// <summary>Спрашивает и возвращает true, только если нажали кнопку действия.</summary>
    /// <param name="headline">Вопрос одной строкой — жирным.</param>
    /// <param name="detail">Что будет последствием. Пусто — строки нет.</param>
    /// <param name="confirmText">Подпись кнопки согласия: само действие.</param>
    /// <param name="cancelText">Подпись кнопки отказа.</param>
    public static bool Ask(Window? owner, string headline, string detail, string confirmText, string cancelText = "Отмена") =>
        Choose(owner, headline, detail, confirmText, null, cancelText) == 1;

    /// <summary>
    /// То же с третьей кнопкой между согласием и отказом. 0 — отказ (и Esc, и крестик),
    /// 1 — кнопка действия, 2 — третья кнопка.
    /// </summary>
    public static int Choose(Window? owner, string headline, string detail, string confirmText, string? extraText, string cancelText = "Отмена")
    {
        var dialog = new ConfirmDialog(headline, detail, confirmText, extraText, cancelText);

        if (owner is { IsLoaded: true })
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dialog.ShowDialog();
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
