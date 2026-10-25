using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using MultiServerSync.Core;
using MultiServerSync.Services;

namespace MultiServerSync.Views.Pages;

public partial class SettingsPage : Page
{
    private bool _ready;

    public SettingsPage()
    {
        InitializeComponent();

        var settings = AppState.Current.Settings;

        ParallelBox.Value = settings.MaxParallel;
        ConnectTimeoutBox.Value = settings.ConnectTimeoutSeconds;
        CommandTimeoutBox.Value = settings.CommandTimeoutSeconds;
        RepeatGuardSwitch.IsChecked = settings.RepeatGuard;

        foreach (ComboBoxItem item in ThemeBox.Items)
        {
            if ((string)item.Tag == settings.Theme)
            {
                ThemeBox.SelectedItem = item;
            }
        }

        ThemeBox.SelectedItem ??= ThemeBox.Items[0];

        VersionText.Text = AppVersion.Details;
        ManifestPathText.Text = "Берётся из " + AppPaths.ManifestFile;

        UserDataPathText.Text = AppPaths.UserDataDir;
        DataPathText.Text = AppPaths.DataDir;

        ShowProtection();


        _ready = true;

        // ярлыки вкладок пишут статистику по открытому разделу
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                AppState.Current.Section = AppSection.Settings;
            }
        };
    }

    /// <summary>Выбор в списке и пояснение — по тому, что действует сейчас.</summary>
    private void ShowProtection()
    {
        var master = PasswordVault.IsMaster;

        _showingProtection = true;
        ProtectionBox.SelectedIndex = master ? 1 : 0;
        _showingProtection = false;

        ChangeMasterButton.Visibility = master ? Visibility.Visible : Visibility.Collapsed;
        ProtectionText.Text = master
            ? "Зашифрованы мастер-паролем: спрашивается при запуске, файл серверов открывается на любом компьютере"
            : "Зашифрованы под вашей учётной записью Windows: ничего не спрашивается, но на другом компьютере пароли придётся ввести заново";
    }

    private bool _showingProtection;

    /// <summary>
    /// Смена режима сразу перешифровывает все пароли. Отказ в окне возвращает список как был.
    /// </summary>
    private void OnProtectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_showingProtection || ProtectionBox.SelectedItem is not ComboBoxItem { Tag: string tag })
        {
            return;
        }

        var wantMaster = tag == "Master";

        if (wantMaster == PasswordVault.IsMaster)
        {
            return;
        }

        var owner = Window.GetWindow(this);

        if (wantMaster)
        {
            if (Dialogs.MasterPasswordDialog.AskNew(owner, change: false) is { } master)
            {
                PasswordVault.EnableMaster(master);
            }
        }
        else if (Dialogs.ConfirmDialog.Ask(
                     owner,
                     "Отключить мастер-пароль?",
                     "Пароли серверов будут зашифрованы под учётной записью Windows: при запуске ничего "
                     + "не спросят, но на другом компьютере пароли придётся ввести заново.",
                     "Отключить"))
        {
            PasswordVault.DisableMaster();
        }

        ShowProtection();
    }

    private void OnChangeMaster(object sender, RoutedEventArgs e)
    {
        if (Dialogs.MasterPasswordDialog.AskNew(Window.GetWindow(this), change: true) is { } master)
        {
            PasswordVault.EnableMaster(master);
        }
    }

    private void OnRepeatGuardChanged(object sender, RoutedEventArgs e)
    {
        var on = RepeatGuardSwitch.IsChecked == true;

        AppState.Current.Settings.RepeatGuard = on;
        AppState.Current.SaveSettings();
        AppLog.Info(on
            ? "Защита от двойного запуска команды включена."
            : "Защита от двойного запуска команды выключена.");
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || ThemeBox.SelectedItem is not ComboBoxItem { Tag: string theme })
        {
            return;
        }

        AppState.Current.Settings.Theme = theme;
        AppState.Current.SaveSettings();
        MainWindow.ApplyTheme(theme);
        AppLog.Info($"Тема переключена: {theme}.");
    }

    private void OnNumbersChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        var settings = AppState.Current.Settings;

        settings.MaxParallel = (int)(ParallelBox.Value ?? settings.MaxParallel);
        settings.ConnectTimeoutSeconds = (int)(ConnectTimeoutBox.Value ?? settings.ConnectTimeoutSeconds);
        settings.CommandTimeoutSeconds = (int)(CommandTimeoutBox.Value ?? settings.CommandTimeoutSeconds);

        AppState.Current.SaveSettings();
    }

    private void OnOpenUserData(object sender, RoutedEventArgs e) => Open(AppPaths.UserDataDir);

    private void OnOpenData(object sender, RoutedEventArgs e) => Open(AppPaths.DataDir);

    private void OnOpenSite(object sender, RoutedEventArgs e) => Open(UpdateService.PageUrl);

    private static void Open(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    // ── обновление ───────────────────────────────────────────────────────────

    private void OnUpdateClick(object sender, RoutedEventArgs e) =>
        Dialogs.UpdateWindow.Open(Window.GetWindow(this));
}
