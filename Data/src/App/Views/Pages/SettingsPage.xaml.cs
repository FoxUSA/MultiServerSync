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

        // громкость не из списка (правили файл руками) — ближайшая из списка
        SoundBox.SelectedItem = SoundBox.Items.Cast<ComboBoxItem>()
            .MinBy(item => Math.Abs(int.Parse((string)item.Tag) - settings.FinishSoundVolume));

        VersionText.Text = AppVersion.Details;
        ManifestPathText.Text = "Берётся из " + AppPaths.ManifestFile;
        ShowGeo();

        // база обновилась — дата в строке следом; подписка только пока страница на экране
        Loaded += (_, _) =>
        {
            GeoBase.Changed -= ShowGeo;
            GeoBase.Changed += ShowGeo;
            ShowGeo();
        };
        Unloaded += (_, _) => GeoBase.Changed -= ShowGeo;

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
        AppLog.Hint(on
            ? "Защита от двойного запуска команды включена."
            : "Защита от двойного запуска команды выключена.");
    }

    private void OnSoundChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || SoundBox.SelectedItem is not ComboBoxItem { Tag: string tag })
        {
            return;
        }

        var volume = int.Parse(tag);

        AppState.Current.Settings.FinishSoundVolume = volume;
        AppState.Current.SaveSettings();
        FinishSound.Preview();
        AppLog.Hint(volume == 0 ? "Звук уведомлений выключен." : $"Громкость звука уведомлений: {volume}%.");
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

    /// <summary>Строка о гео-базе: чьи данные (условие GeoLite2 — указать MaxMind) и от какого числа.</summary>
    private void ShowGeo() =>
        GeoText.Text = "Гео серверов — данные GeoLite2 от MaxMind, https://www.maxmind.com"
            + (GeoBase.DateText is { } date ? $" · база от {date}" : " · база ещё не скачана");

    /// <summary>
    /// «Обновить»: сначала гео-база (новая есть — качается, ход на кнопке), потом программа: есть свежая на
    /// byfox.dev — сразу окно с ходом обновления (программа и лаунчер); нечего ставить — плашка.
    /// </summary>
    private async void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        UpdateButton.Content = "Проверка…";

        try
        {
            var geo = await GeoBase.UpdateAsync(force: true, new Progress<int>(pct => UpdateButton.Content = $"Гео-база {pct}%"));
            ShowGeo();

            if (geo.Updated)
            {
                AppLog.Hint($"Гео-база обновлена: GeoLite2 от {GeoBase.DateText}.");
            }
            else if (geo.Error is { } why)
            {
                AppLog.Hint("Гео-базу обновить не удалось: " + why, LogLevel.Warning);
            }

            UpdateButton.Content = "Проверка…";

            if (await UpdateService.CheckAsync() is not null)
            {
                Dialogs.UpdateWindow.Open(Window.GetWindow(this), start: true);
            }
            else if (!geo.Updated)
            {
                AppLog.Hint($"Обновлений нет: программа {AppVersion.Version} последняя"
                    + (GeoBase.DateText is { } date ? $", гео-база от {date} свежая." : "."));
            }
        }
        catch (Exception ex)
        {
            AppLog.Hint("Проверить обновления не удалось: " + ex.Message, LogLevel.Warning);
        }
        finally
        {
            UpdateButton.IsEnabled = true;
            UpdateButton.Content = "Обновить";
        }
    }
}
