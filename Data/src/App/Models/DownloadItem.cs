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

    public bool IsSelected { get; set; } = true;
}

/// <summary>
/// Что скачать: файл или папка на конкретном сервере — выбраны обзором этого сервера.
/// Зеркало строки копирования: галочка решает, участвует ли строка.
/// </summary>
public sealed class DownloadItem : ObservableObject
{
    private bool _isSelected = true;

    public DownloadItem(string serverId, string path, bool isDirectory, long size, DateTime modified)
    {
        ServerId = serverId;
        Path = path;
        IsDirectory = isDirectory;
        SizeBytes = isDirectory ? -1 : size;
        Modified = modified;

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

    /// <summary>Размер папки на сервере заранее не известен — только у файла.</summary>
    public long SizeBytes { get; }

    public string SizeText => SizeBytes < 0 ? "—" : TransferItem.Format(SizeBytes);

    /// <summary>Дата на момент выбора в обзоре.</summary>
    public DateTime Modified { get; }

    public string ModifiedText => Modified.Year > 1970 ? Modified.ToString("dd.MM.yyyy HH:mm") : "—";

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
        IsSelected = IsSelected,
    };

    public static DownloadItem From(SavedDownloadItem saved) =>
        new(saved.ServerId, saved.Path, saved.IsDirectory, saved.Size, saved.Modified) { _isSelected = saved.IsSelected };
}
