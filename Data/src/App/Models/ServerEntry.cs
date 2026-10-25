using System.Text.Json.Serialization;
using MultiServerSync.Core;

namespace MultiServerSync.Models;

public sealed class ServerEntry : ObservableObject
{
    private string _name = string.Empty;
    private string _host = string.Empty;
    private int _port = 22;
    private string _user = "root";
    private string _password = string.Empty;
    private string? _stored;
    private string _keyPath = string.Empty;
    private string _keyPassphrase = string.Empty;
    private string? _storedKeyPassphrase;
    private bool _isSelected;
    private string _lastStatus = string.Empty;
    private string _notes = string.Empty;
    private bool _showNotesInPane = true;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Набор (вкладка раздела «Серверы»), в котором живёт сервер. Пусто — файл старой версии,
    /// где набор был один: такой сервер при загрузке попадает в основной.
    /// </summary>
    public string SetId { get; set; } = string.Empty;

    /// <summary>Понятное отображаемое имя.</summary>
    public string Name
    {
        get => _name;
        set { if (Set(ref _name, value)) { RaiseMany(nameof(Display), nameof(PaneLabel)); } }
    }

    public string Host
    {
        get => _host;
        set { if (Set(ref _host, value)) { RaiseMany(nameof(Display), nameof(PaneLabel)); } }
    }

    public int Port
    {
        get => _port;
        set { if (Set(ref _port, value)) { Raise(nameof(Display)); } }
    }

    public string User
    {
        get => _user;
        set => Set(ref _user, value);
    }

    /// <summary>Пароль открытым текстом — только в памяти. В файл уходит <see cref="StoredPassword"/>.</summary>
    [JsonIgnore]
    public string Password
    {
        get => _password;
        set
        {
            // введён новый пароль — нерасшифрованное старое значение больше не нужно
            if (Set(ref _password, value) && !string.IsNullOrEmpty(value))
            {
                _stored = null;
            }
        }
    }

    /// <summary>
    /// Пароль как он лежит в servers.json — зашифрованным. Пока не расшифрован (другой ПК,
    /// другая учётная запись Windows), сохраняется ровно как был: синхронизация папки
    /// не должна затереть пароль на том компьютере, где он открывается.
    /// </summary>
    [JsonPropertyName("Password")]
    public string StoredPassword
    {
        get => string.IsNullOrEmpty(_password) ? _stored ?? string.Empty : Secrets.Protect(_password);
        set => _stored = value;
    }

    /// <summary>
    /// Закрытый ключ SSH — путь к файлу (OpenSSH, PEM или PuTTY .ppk). Пусто — вход по паролю.
    /// С ключом пароль сервера нужен только для sudo.
    /// </summary>
    public string KeyPath
    {
        get => _keyPath;
        set => Set(ref _keyPath, value ?? string.Empty);
    }

    /// <summary>Пароль от самого ключа, если он им защищён. Хранится так же, как пароль сервера.</summary>
    [JsonIgnore]
    public string KeyPassphrase
    {
        get => _keyPassphrase;
        set
        {
            if (Set(ref _keyPassphrase, value) && !string.IsNullOrEmpty(value))
            {
                _storedKeyPassphrase = null;
            }
        }
    }

    [JsonPropertyName("KeyPassphrase")]
    public string StoredKeyPassphrase
    {
        get => string.IsNullOrEmpty(_keyPassphrase) ? _storedKeyPassphrase ?? string.Empty : Secrets.Protect(_keyPassphrase);
        set => _storedKeyPassphrase = value;
    }

    [JsonIgnore]
    public bool HasKey => !string.IsNullOrWhiteSpace(KeyPath);

    private string _jumpServerId = string.Empty;

    /// <summary>
    /// Промежуточный сервер (jump host, как ProxyJump у ssh): подключение идёт через него.
    /// Пусто — напрямую.
    /// </summary>
    public string JumpServerId
    {
        get => _jumpServerId;
        set => Set(ref _jumpServerId, value ?? string.Empty);
    }

    /// <summary>
    /// Расшифровать пароль и пароль ключа после загрузки. false — что-то не вышло,
    /// зашифрованное значение сохранено.
    /// </summary>
    /// <param name="resave">Значение лежит не в текущем режиме (или открытым текстом) — файл стоит пересохранить.</param>
    public bool OpenPassword(out bool resave)
    {
        resave = false;

        var opened = Open(ref _password, ref _stored, ref resave);
        opened &= Open(ref _keyPassphrase, ref _storedKeyPassphrase, ref resave);
        return opened;
    }

    private static bool Open(ref string plain, ref string? stored, ref bool resave)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return true;
        }

        if (Secrets.Unprotect(stored) is not { } open)
        {
            return false;
        }

        resave |= !Secrets.IsCurrent(stored);
        plain = open;
        stored = null;
        return true;
    }

    /// <summary>Сброс мастер-пароля: пароли стираются вместе с нерасшифрованными значениями.</summary>
    public void ForgetPassword()
    {
        _stored = null;
        _storedKeyPassphrase = null;
        Password = string.Empty;
        KeyPassphrase = string.Empty;
    }

    /// <summary>
    /// Отметка первой вкладки. Живые галочки лежат в самих вкладках (у каждой свои),
    /// здесь остаётся копия с первой вкладки — чтобы servers.json не расходился с тем, что видно,
    /// и чтобы перенести отметки со старой версии программы.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>Заметки в свободной форме: что за сервер, где лежит, что на нём крутится.</summary>
    public string Notes
    {
        get => _notes;
        set { if (Set(ref _notes, value ?? string.Empty)) { RaiseMany(nameof(NotesLine), nameof(HasNotes), nameof(PaneNotesVisible)); } }
    }

    /// <summary>Показывать заметку под именем в панели слева. По умолчанию — да.</summary>
    public bool ShowNotesInPane
    {
        get => _showNotesInPane;
        set { if (Set(ref _showNotesInPane, value)) { Raise(nameof(PaneNotesVisible)); } }
    }

    /// <summary>Строка заметки в панели слева: есть что показать и показ не выключен.</summary>
    [JsonIgnore]
    public bool PaneNotesVisible => ShowNotesInPane && HasNotes;

    /// <summary>Заметки одной строкой — для таблицы и боковой панели, где места на абзацы нет.</summary>
    [JsonIgnore]
    public string NotesLine => string.Join(
        " ",
        Notes.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    [JsonIgnore]
    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);

    /// <summary>Результат последней проверки связи — только для показа, не сохраняется.</summary>
    [JsonIgnore]
    public string LastStatus
    {
        get => _lastStatus;
        set => Set(ref _lastStatus, value);
    }

    [JsonIgnore]
    public string Display => string.IsNullOrWhiteSpace(Name)
        ? $"{Host}:{Port}"
        : $"{Name} ({Host}:{Port})";

    /// <summary>Подпись для текста, который уходит наружу: порт показывается только нестандартный.</summary>
    [JsonIgnore]
    public string Caption
    {
        get
        {
            var address = Port is 22 or <= 0 ? Host : $"{Host}:{Port}";
            return string.IsNullOrWhiteSpace(Name) ? address : $"{Name} ({address})";
        }
    }

    /// <summary>Короткая подпись для списка в боковой панели.</summary>
    [JsonIgnore]
    public string PaneLabel => string.IsNullOrWhiteSpace(Name) ? Host : Name;

    public ServerEntry Clone() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        SetId = SetId,
        Name = Name,
        Host = Host,
        Port = Port,
        User = User,
        Password = Password,
        KeyPath = KeyPath,
        KeyPassphrase = KeyPassphrase,
        JumpServerId = JumpServerId,
        Notes = Notes,
        ShowNotesInPane = ShowNotesInPane,
        IsSelected = IsSelected,
    };
}
