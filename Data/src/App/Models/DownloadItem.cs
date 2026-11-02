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

    public RowMemory? Smart { get; set; }
}

/// <summary>
/// Что скачать: файл или папка на конкретном сервере — выбраны обзором этого сервера.
/// Зеркало строки копирования: галочка решает, участвует ли строка.
/// </summary>
public sealed class DownloadItem : ObservableObject, IRowCheck
{
    private bool _isSelected = true;
    private long _sizeBytes;
    private DateTime _modified;
    private int _files = -1;
    private bool _isMissing;
    private long _keptBytes = -1;
    private int _keptFiles = -1;

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

    public RowMemory Memory { get; set; } = new();

    /// <summary>
    /// Дата строки для кнопки «Нескачанные» — из обычной проверки списка, отдельно на сервер за ней не ходим.
    /// Null — проверки ещё не было или пути больше нет.
    /// </summary>
    public string? ChangeDate =>
        _isMissing || _modified.Year <= 1970 || (IsDirectory && _files < 0)
            ? null
            : _modified.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss");

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
        : _sizeBytes < 0 ? "—" : TransferItem.Format(_sizeBytes);

    /// <summary>Файлов в строке — для столбца и его сортировки: у файла один, у папки после проверки; -1 — не знаем.</summary>
    public int FilesCount => _isMissing ? -1 : IsDirectory ? _files : 1;

    public string FilesText => FilesCount < 0 ? "—" : FilesCount.ToString();

    /// <summary>Размер того, что пройдёт исключения окна; -1 — ещё не считали.</summary>
    public long KeptBytes => _keptBytes;

    public string KeptSizeText => _keptBytes < 0 ? "—" : TransferItem.Format(_keptBytes);

    /// <summary>Файлов, которые пройдут исключения окна; -1 — ещё не считали.</summary>
    public int KeptFiles => _keptFiles;

    public string KeptFilesText => _keptFiles < 0 ? "—" : _keptFiles.ToString();

    /// <summary>Дата изменения: у файла — своя, у папки — самого свежего файла внутри.</summary>
    public DateTime Modified => _modified;

    public string ModifiedText => !_isMissing && _modified.Year > 1970 ? _modified.ToString("dd.MM.yyyy HH:mm") : "—";

    /// <summary>Свежие сведения с сервера — после проверки списка.</summary>
    public void Update(long size, DateTime modified, int files, bool missing, long keptBytes, int keptFiles)
    {
        _sizeBytes = size;
        _modified = modified;
        _files = files;
        _isMissing = missing;
        _keptBytes = keptBytes;
        _keptFiles = keptFiles;
        RaiseMany(
            nameof(SizeBytes), nameof(Files), nameof(IsMissing), nameof(SizeText), nameof(Modified), nameof(ModifiedText),
            nameof(FilesCount), nameof(FilesText), nameof(KeptBytes), nameof(KeptSizeText), nameof(KeptFiles), nameof(KeptFilesText));
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
        Smart = Memory.Copy(),
    };

    public static DownloadItem From(SavedDownloadItem saved) =>
        new(saved.ServerId, saved.Path, saved.IsDirectory, saved.Size, saved.Modified)
        {
            _isSelected = saved.IsSelected,
            _files = saved.IsDirectory ? saved.Files : -1,
            _sizeBytes = saved.IsDirectory && saved.Files < 0 ? -1 : saved.Size,
            Memory = saved.Smart?.Copy() ?? new(),
        };
}
