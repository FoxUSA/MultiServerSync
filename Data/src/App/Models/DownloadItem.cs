using MultiServerSync.Core;
using MultiServerSync.Services;

namespace MultiServerSync.Models;

/// <summary>Строка скачивания как она лежит в файле.</summary>
public sealed class SavedDownloadItem
{
    public string ServerId { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public bool IsDirectory { get; set; }

    public long Size { get; set; }

    public DateTime Modified { get; set; }

    /// <summary>Файлов в папке при последней проверке; -1 — ещё не считали.</summary>
    public int Files { get; set; } = -1;

    public bool IsSelected { get; set; } = true;
}

/// <summary>
/// Что скачать: файл или папка на конкретном сервере — выбраны обзором этого сервера.
/// Зеркало строки копирования: галочка решает, участвует ли строка.
/// </summary>
public sealed class DownloadItem : ObservableObject
{
    private bool _isSelected = true;
    private long _sizeBytes;
    private DateTime _modified;
    private int _files = -1;
    private bool _isMissing;

    public DownloadItem(string serverId, string path, bool isDirectory, long size, DateTime modified)
    {
        ServerId = serverId;
        Path = path;
        IsDirectory = isDirectory;
        _sizeBytes = isDirectory ? -1 : size;
        _modified = modified;

        var trimmed = path.TrimEnd('/');
        Name = trimmed.Length == 0 ? "/" : trimmed[(trimmed.LastIndexOf('/') + 1)..];
    }

    /// <summary>Галочка «эта строка участвует в скачивании».</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public string ServerId { get; }

    public string Path { get; }

    public string Name { get; }

    public bool IsDirectory { get; }

    public string Kind => IsDirectory ? "Папка" : "Файл";

    /// <summary>Размер: у файла — свой, у папки — всех файлов внутри после проверки; -1 — не знаем.</summary>
    public long SizeBytes => _sizeBytes;

    /// <summary>Файлов в папке после проверки; -1 — ещё не считали.</summary>
    public int Files => _files;

    /// <summary>На сервере пути больше нет.</summary>
    public bool IsMissing => _isMissing;

    public string SizeText => _isMissing
        ? "нет на сервере"
        : IsDirectory && _files >= 0
            ? $"{_files} файл(ов), {TransferItem.Format(Math.Max(0, _sizeBytes))}"
            : _sizeBytes < 0 ? "—" : TransferItem.Format(_sizeBytes);

    /// <summary>Дата изменения: у файла — своя, у папки — самого свежего файла внутри.</summary>
    public DateTime Modified => _modified;

    public string ModifiedText => !_isMissing && _modified.Year > 1970 ? _modified.ToString("dd.MM.yyyy HH:mm") : "—";

    /// <summary>Свежие сведения с сервера — после проверки списка.</summary>
    public void Update(long size, DateTime modified, int files, bool missing)
    {
        _sizeBytes = size;
        _modified = modified;
        _files = files;
        _isMissing = missing;
        RaiseMany(nameof(SizeBytes), nameof(Files), nameof(IsMissing), nameof(SizeText), nameof(Modified), nameof(ModifiedText));
    }

    public ServerEntry? Server => AppState.Current.Servers.FirstOrDefault(s => s.Id == ServerId);

    /// <summary>Сервер удалили из списка — строка остаётся, но скачать её не с чего.</summary>
    public string ServerLabel => Server?.PaneLabel ?? "сервер удалён";

    public SavedDownloadItem ToSaved() => new()
    {
        ServerId = ServerId,
        Path = Path,
        IsDirectory = IsDirectory,
        Size = SizeBytes,
        Modified = Modified,
        Files = Files,
        IsSelected = IsSelected,
    };

    public static DownloadItem From(SavedDownloadItem saved) =>
        new(saved.ServerId, saved.Path, saved.IsDirectory, saved.Size, saved.Modified)
        {
            _isSelected = saved.IsSelected,
            _files = saved.IsDirectory ? saved.Files : -1,
            _sizeBytes = saved.IsDirectory && saved.Files < 0 ? -1 : saved.Size,
        };
}
