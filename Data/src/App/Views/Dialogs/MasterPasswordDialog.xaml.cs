using System.Windows;
using MultiServerSync.Services;
using Wpf.Ui.Controls;

namespace MultiServerSync.Views.Dialogs;

/// <summary>
/// Мастер-пароль: ввод при запуске или задание нового (включение и смена в «Настройках»).
/// </summary>
public partial class MasterPasswordDialog : FluentWindow
{
    /// <summary>Короче — подбирается слишком быстро.</summary>
    private const int MinLength = 6;

    private readonly bool _unlock;

    private MasterPasswordDialog(bool unlock, string headline, string detail, string confirmText, string cancelText)
    {
        InitializeComponent();

        _unlock = unlock;
        HeadlineText.Text = headline;
        DetailText.Text = detail;
        ConfirmButton.Content = confirmText;
        CancelButton.Content = cancelText;

        // при запуске пароль один и есть выход «не помню»; при задании — два поля, сверяются
        SecondBox.Visibility = unlock ? Visibility.Collapsed : Visibility.Visible;
        ForgotButton.Visibility = unlock ? Visibility.Visible : Visibility.Collapsed;

        Loaded += (_, _) => FirstBox.Focus();
    }

    /// <summary>Новый мастер-пароль. null — передумали.</summary>
    public string? NewPassword { get; private set; }

    /// <summary>
    /// Ввод при запуске. true — пароль верный (или сброшен через «Не помню пароль»),
    /// false — выход из программы.
    /// </summary>
    public static bool Unlock()
    {
        var dialog = new MasterPasswordDialog(
            unlock: true,
            "Введите мастер-пароль",
            "Им зашифрованы пароли серверов.",
            "Открыть",
            "Выход")
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = true,
        };

        return dialog.ShowDialog() == true;
    }

    /// <summary>Задать новый мастер-пароль. null — отменили.</summary>
    public static string? AskNew(Window? owner, bool change)
    {
        var dialog = new MasterPasswordDialog(
            unlock: false,
            change ? "Новый мастер-пароль" : "Включить мастер-пароль",
            "Будет спрашиваться при каждом запуске. Забудете — пароли серверов придётся ввести заново, "
            + "сами серверы останутся.",
            "Сохранить",
            "Отмена")
        {
            Owner = owner,
            ShowInTaskbar = false,
        };

        return dialog.ShowDialog() == true ? dialog.NewPassword : null;
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        var first = FirstBox.Password ?? string.Empty;

        if (_unlock)
        {
            if (first.Length > 0 && PasswordVault.TryUnlock(first))
            {
                DialogResult = true;
                return;
            }

            Hint("Пароль не подошёл.");
            FirstBox.Password = string.Empty;
            FirstBox.Focus();
            return;
        }

        if (first.Length < MinLength)
        {
            Hint($"Не короче {MinLength} символов.");
            FirstBox.Focus();
            return;
        }

        if (first != SecondBox.Password)
        {
            Hint("Пароли не совпадают.");
            SecondBox.Password = string.Empty;
            SecondBox.Focus();
            return;
        }

        NewPassword = first;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>Без мастер-пароля пароли серверов не расшифровать — только стереть их и жить дальше.</summary>
    private void OnForgot(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDialog.Ask(
                this,
                "Сбросить мастер-пароль?",
                "Пароли всех серверов будут стёрты — без мастер-пароля их не расшифровать. "
                + "Сами серверы, наборы и вкладки останутся, пароли нужно будет ввести заново.",
                "Сбросить"))
        {
            return;
        }

        PasswordVault.Forget();
        DialogResult = true;
    }

    private void Hint(string text)
    {
        HintText.Text = text;
        HintText.Visibility = Visibility.Visible;
    }
}
