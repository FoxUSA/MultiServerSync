using System.Windows;
using System.Windows.Input;
using MultiServerSync.Models;
using MultiServerSync.Services;
using Wpf.Ui.Controls;

namespace MultiServerSync.Views.Dialogs;

public partial class ServerEditDialog : FluentWindow
{
    private readonly ServerEntry _target;

    public ServerEditDialog(ServerEntry entry, bool isNew)
    {
        InitializeComponent();

        _target = entry;
        Title = isNew ? "Новый сервер" : "Сервер: " + entry.Display;

        NameBox.Text = entry.Name;
        HostBox.Text = entry.Host;
        DataObject.AddPastingHandler(HostBox, OnHostPaste);
        PortBox.Value = entry.Port <= 0 ? 22 : entry.Port;
        UserBox.Text = string.IsNullOrWhiteSpace(entry.User) ? "root" : entry.User;
        PasswordBox.Password = entry.Password;
        KeyBox.Text = entry.KeyPath;
        KeyPassBox.Password = entry.KeyPassphrase;

        // промежуточный — любой другой сервер; сам себе он не промежуточный
        var jumps = new List<JumpOption> { new(string.Empty, "— напрямую —") };
        jumps.AddRange(AppState.Current.Servers
            .Where(s => s.Id != entry.Id)
            .OrderBy(s => s.PaneLabel, StringComparer.OrdinalIgnoreCase)
            .Select(s => new JumpOption(s.Id, s.Display)));
        JumpBox.ItemsSource = jumps;
        JumpBox.SelectedItem = jumps.FirstOrDefault(j => j.Id == entry.JumpServerId) ?? jumps[0];
        NotesBox.Text = entry.Notes;
        ShowNotesBox.IsChecked = entry.ShowNotesInPane;

        Loaded += (_, _) =>
        {
            ShowHostKey();
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private ServerEntry Snapshot() => new()
    {
        Name = NameBox.Text.Trim(),
        Host = HostBox.Text.Trim(),
        Port = (int)(PortBox.Value ?? 22),
        User = UserBox.Text.Trim(),
        Password = PasswordBox.Password,
        KeyPath = KeyBox.Text.Trim(),
        KeyPassphrase = KeyPassBox.Password,
        JumpServerId = (JumpBox.SelectedItem as JumpOption)?.Id ?? string.Empty,
    };

    /// <summary>Строка списка «Через сервер».</summary>
    private sealed record JumpOption(string Id, string Label);

    private bool Validate(out string error)
    {
        if (string.IsNullOrWhiteSpace(HostBox.Text))
        {
            error = "Укажите IP-адрес.";
            return false;
        }

        var host = HostBox.Text.Trim();

        if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            error = "Адрес — IPv4 (203.0.113.10), IPv6 (2001:db8::10) или имя (web1.example.com).";
            return false;
        }

        var port = (int)(PortBox.Value ?? 0);
        if (port is < 1 or > 65535)
        {
            error = "Порт должен быть от 1 до 65535.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(UserBox.Text))
        {
            error = "Укажите пользователя.";
            return false;
        }

        // ключ проверяем сразу: неверный пароль ключа лучше узнать здесь, а не при запуске команды
        if (!string.IsNullOrWhiteSpace(KeyBox.Text))
        {
            try
            {
                (SshService.OpenKey(KeyBox.Text, KeyPassBox.Password) as IDisposable)?.Dispose();
            }
            catch (SshKeyException ex)
            {
                error = ex.Message;
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    /// <summary>Выбор файла ключа: окно открывается в %USERPROFILE%\.ssh, если такая папка есть.</summary>
    private void OnPickKey(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Закрытый ключ SSH",
            Filter = "Все файлы|*.*|Ключи PuTTY (*.ppk)|*.ppk|Ключи PEM (*.pem)|*.pem",
            CheckFileExists = true,
        };

        var current = KeyBox.Text.Trim();

        if (current.Length > 0 && System.IO.Path.GetDirectoryName(current) is { } folder && System.IO.Directory.Exists(folder))
        {
            dialog.InitialDirectory = folder;
        }
        else if (System.IO.Directory.Exists(SshService.UserSshFolder))
        {
            dialog.InitialDirectory = SshService.UserSshFolder;
        }

        if (dialog.ShowDialog(this) == true)
        {
            // частая ошибка — выбрать открытую часть: подставляем закрытую рядом с ней
            var path = dialog.FileName;

            if (path.EndsWith(".pub", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(path[..^4]))
            {
                path = path[..^4];
            }

            KeyBox.Text = path;
        }
    }

    // ── адрес ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Вставка: «203.0.113.10:2222», «web1:2222» и «[2001:db8::10]:2222» раскладываются
    /// на адрес и порт. Голый IPv6 (двоеточий больше одного) идёт в поле целиком.
    /// Пробелы и «user@» спереди отбрасываются.
    /// </summary>
    private void OnHostPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(DataFormats.UnicodeText) is not string pasted)
        {
            return;
        }

        e.CancelCommand();

        var text = new string(pasted.Where(c => !char.IsWhiteSpace(c)).ToArray());
        text = text[(text.LastIndexOf('@') + 1)..];

        string host;
        string? port = null;

        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']');
            host = close > 0 ? text[1..close] : text.Trim('[', ']');
            port = close > 0 && close + 1 < text.Length && text[close + 1] == ':' ? text[(close + 2)..] : null;
        }
        else if (text.Count(c => c == ':') == 1)
        {
            host = text[..text.IndexOf(':')];
            port = text[(text.IndexOf(':') + 1)..];
        }
        else
        {
            host = text;
        }

        if (int.TryParse(port, out var number) && number is >= 1 and <= 65535)
        {
            PortBox.Value = number;
        }

        HostBox.SelectedText = host;
        HostBox.SelectionStart += host.Length;
        HostBox.SelectionLength = 0;
    }

    /// <summary>Строка с ключом сервера: запомненный отпечаток или «сервер показал другой».</summary>
    private void ShowHostKey()
    {
        var probe = Snapshot();

        if (string.IsNullOrWhiteSpace(probe.Host))
        {
            HostKeyPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var known = HostKeys.Get(probe);
        var fresh = HostKeys.Mismatch(probe);

        if (fresh is not null)
        {
            HostKeyText.Text = $"Ключ сервера изменился: запомнен {known?.Fingerprint}, сейчас {fresh.Fingerprint}.";
            HostKeyButton.Content = "Принять новый";
            HostKeyButton.ToolTip = "Если сервер переустанавливали. Иначе кто-то подменяет сервер — не принимайте.";
        }
        else if (known is not null)
        {
            HostKeyText.Text = $"Ключ сервера: {known.Algorithm} {known.Fingerprint}";
            HostKeyButton.Content = "Забыть";
            HostKeyButton.ToolTip = "Следующее подключение запомнит ключ, который покажет сервер.";
        }
        else
        {
            HostKeyPanel.Visibility = Visibility.Collapsed;
            return;
        }

        HostKeyPanel.Visibility = Visibility.Visible;
    }

    private void OnHostKeyAction(object sender, RoutedEventArgs e)
    {
        var probe = Snapshot();

        if (HostKeys.Mismatch(probe) is not null)
        {
            HostKeys.AcceptNew(probe);
        }
        else
        {
            HostKeys.Forget(probe);
        }

        ShowHostKey();
    }

    private void Show(InfoBarSeverity severity, string title, string message)
    {
        TestResult.Severity = severity;
        TestResult.Title = title;
        TestResult.Message = message;
        TestResult.IsOpen = true;
    }

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        if (!Validate(out var error))
        {
            Show(InfoBarSeverity.Warning, "Не хватает данных", error);
            return;
        }

        var probe = Snapshot();
        var settings = AppState.Current.Settings;

        TestButton.IsEnabled = false;
        TestSpinner.Visibility = Visibility.Visible;
        Show(InfoBarSeverity.Informational, "Подключение…", $"{probe.Host}:{probe.Port} под пользователем {probe.User}");

        try
        {
            var outcome = await SshService.RunCommandAsync(
                probe, settings, "uname -a", CancellationToken.None);

            var info = string.IsNullOrWhiteSpace(outcome.StdOut) ? outcome.StdErr : outcome.StdOut;
            Show(InfoBarSeverity.Success, "Подключение работает", info.Trim());
            AppLog.Success($"Проверка из диалога прошла: {probe.Display}");
        }
        catch (Exception ex)
        {
            var explained = SshService.Describe(ex, probe);

            if (ex is Renci.SshNet.Common.SshAuthenticationException)
            {
                var methods = await SshService.TryProbeAuthMethodsAsync(probe, settings);

                if (methods.Count > 0)
                {
                    explained += $"\nСервер разрешает: {string.Join(", ", methods)}.";

                    if (!probe.HasKey && !methods.Contains("password") && !methods.Contains("keyboard-interactive"))
                    {
                        explained += " Вход по паролю отключён — укажите ключ SSH.";
                    }
                    else if (probe.HasKey && !methods.Contains("publickey"))
                    {
                        explained += " Вход по ключу на сервере отключён.";
                    }
                }
            }

            Show(InfoBarSeverity.Error, "Не подключилось", explained);
            AppLog.Error($"Проверка из диалога не прошла: {explained}", probe.Display);
        }
        finally
        {
            TestButton.IsEnabled = true;
            TestSpinner.Visibility = Visibility.Collapsed;
            ShowHostKey();
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!Validate(out var error))
        {
            Show(InfoBarSeverity.Warning, "Не хватает данных", error);
            return;
        }

        _target.Name = NameBox.Text.Trim();
        _target.Host = HostBox.Text.Trim();
        _target.Port = (int)(PortBox.Value ?? 22);
        _target.User = UserBox.Text.Trim();
        _target.Password = PasswordBox.Password;
        _target.KeyPath = KeyBox.Text.Trim();
        _target.KeyPassphrase = KeyPassBox.Password;
        _target.JumpServerId = (JumpBox.SelectedItem as JumpOption)?.Id ?? string.Empty;
        _target.Notes = NotesBox.Text.Trim();
        _target.ShowNotesInPane = ShowNotesBox.IsChecked == true;

        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
