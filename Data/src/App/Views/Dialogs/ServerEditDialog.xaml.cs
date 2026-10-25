using System.Windows;
using System.Windows.Input;
using MultiServerSync.Models;
using MultiServerSync.Services;
using Wpf.Ui.Controls;

namespace MultiServerSync.Views.Dialogs;

public partial class ServerEditDialog : FluentWindow
{
    private readonly ServerEntry _target;

    // адрес, с которым запись пришла: старые записи могли быть с именем хоста вместо IP
    private readonly string _originalHost;

    public ServerEditDialog(ServerEntry entry, bool isNew)
    {
        InitializeComponent();

        _target = entry;
        Title = isNew ? "Новый сервер" : "Сервер: " + entry.Display;

        NameBox.Text = entry.Name;
        HostBox.Text = entry.Host;
        _originalHost = entry.Host.Trim();
        DataObject.AddPastingHandler(HostBox, OnHostPaste);
        PortBox.Value = entry.Port <= 0 ? 22 : entry.Port;
        UserBox.Text = string.IsNullOrWhiteSpace(entry.User) ? "root" : entry.User;
        PasswordBox.Password = entry.Password;
        KeyBox.Text = entry.KeyPath;
        KeyPassBox.Password = entry.KeyPassphrase;
        NotesBox.Text = entry.Notes;
        ShowNotesBox.IsChecked = entry.ShowNotesInPane;

        Loaded += (_, _) =>
        {
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
    };

    private bool Validate(out string error)
    {
        if (string.IsNullOrWhiteSpace(HostBox.Text))
        {
            error = "Укажите IP-адрес.";
            return false;
        }

        var host = HostBox.Text.Trim();

        if (host != _originalHost && !IsIpv4(host))
        {
            error = "IP-адрес — четыре числа от 0 до 255 через точку, например 203.0.113.10.";
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

    // ── ввод IP ──────────────────────────────────────────────────────────────

    private static bool IsIpv4(string text)
    {
        var parts = text.Split('.');

        return parts.Length == 4
            && parts.All(p => p.Length is > 0 and <= 3 && p.All(char.IsAsciiDigit) && int.Parse(p) <= 255);
    }

    /// <summary>Пробел не доходит до PreviewTextInput — глушим его здесь.</summary>
    private void OnHostPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            e.Handled = true;
        }
    }

    private void OnHostPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = true;

        foreach (var c in e.Text)
        {
            TypeHostChar(c);
        }
    }

    /// <summary>
    /// Набор адреса: пропускаются только цифры и точки. Когда число дописано
    /// (три цифры, ноль или следующая цифра дала бы больше 255), точка ставится сама,
    /// а набранная следом точка поглощается — работает и с привычкой ставить её руками.
    /// </summary>
    private void TypeHostChar(char c)
    {
        if (!char.IsAsciiDigit(c) && c != '.')
        {
            return;
        }

        var text = HostBox.Text;
        var start = HostBox.SelectionStart;
        var head = text[..start];
        var tail = text[(start + HostBox.SelectionLength)..];

        if (tail.Length > 0)
        {
            // правка в середине адреса: без подстановок, просто символ
            Put(head + c, tail);
            return;
        }

        var octets = head.Split('.');
        var current = octets[^1];

        if (c == '.')
        {
            if (current.Length > 0 && octets.Length < 4)
            {
                Put(head + '.', tail);
            }

            return;
        }

        if (current.Length > 0 && !CanGrow(current, c))
        {
            // число уже полное — цифра начинает следующее
            if (octets.Length >= 4)
            {
                return;
            }

            head += '.';
            octets = head.Split('.');
        }

        head += c;
        current = head.Split('.')[^1];

        if (octets.Length < 4 && IsComplete(current))
        {
            head += '.';
        }

        Put(head, tail);

        static bool CanGrow(string octet, char digit) =>
            octet.Length < 3 && octet != "0" && int.Parse(octet + digit) <= 255;

        // дальше дописать нечего: любая следующая цифра выведет за 255
        static bool IsComplete(string octet) =>
            octet.Length == 3 || octet == "0" || int.Parse(octet) * 10 > 255;
    }

    private void Put(string head, string tail)
    {
        HostBox.Text = head + tail;
        HostBox.SelectionStart = head.Length;
        HostBox.SelectionLength = 0;
    }

    /// <summary>
    /// Вставка: из «203.0.113.10:2222» адрес уходит в поле, а порт — в своё поле.
    /// Всё, кроме цифр и точек, выкидывается.
    /// </summary>
    private void OnHostPaste(object sender, DataObjectPastingEventArgs e)
    {
        e.CancelCommand();

        if (e.DataObject.GetData(DataFormats.UnicodeText) is not string pasted)
        {
            return;
        }

        pasted = pasted.Trim();
        var colon = pasted.LastIndexOf(':');

        if (colon > 0 && int.TryParse(pasted[(colon + 1)..], out var port) && port is >= 1 and <= 65535)
        {
            PortBox.Value = port;
            pasted = pasted[..colon];
        }

        var clean = new string(pasted.Where(c => char.IsAsciiDigit(c) || c == '.').ToArray());

        var start = HostBox.SelectionStart;
        var text = HostBox.Text;
        Put(text[..start] + clean, text[(start + HostBox.SelectionLength)..]);
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
