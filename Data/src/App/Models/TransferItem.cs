using System.IO;
using MultiServerSync.Core;

namespace MultiServerSync.Models;

/// <summary>Строка таблицы окна с галочкой: копирование и скачивание.</summary>
public interface IRowCheck
{
    bool IsSelected { get; set; }

    string Name { get; }

    RowMemory Memory { get; }
}

/// <summary>
/// Что кнопка «Незалитые» помнит о строке: только даты и только своё копирование или скачивание.
/// Что лежит на сервере, неважно, и ради этого туда никто не ходит.
/// </summary>
public sealed class RowMemory
{
    /// <summary>Дата строки при последнем удачном заходе; null — этой программой ещё не копировали.</summary>
    public string? Date { get; set; }

    /// <summary>Галочка до нажатия кнопки; null — кнопка не нажата.</summary>
    public bool? Before { get; set; }

    /// <summary>Какой галочку поставила кнопка: разошлась с ней по вине человека — рамка кнопки гаснет.</summary>
    public bool Marked { get; set; }

    public RowMemory Copy() => new() { Date = Date, Before = Before, Marked = Marked };
}

/// <summary>Локальный файл или папка, выбранные для отправки на серверы.</summary>
public sealed class TransferItem : ObservableObject, IRowCheck
{
    private bool _isSelected = true;
    private long _sizeBytes = -1;
    private int _files = -1;
    private long _keptBytes = -1;
    private int _keptFiles = -1;
    private DateTime _modified;

    /// <param name="filter">Исключения окна, куда встаёт строка: по ним считаются «Размер*» и «Файлов*».</param>
    public TransferItem(string path, ExcludeFilter? filter = null)
    {
        FullPath = path;
        IsDirectory = Directory.Exists(path);
        Name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));

        if (string.IsNullOrEmpty(Name))
        {
            Name = path;
        }

        Refresh(filter);
    }

    /// <summary>Галочка «этот объект участвует в копировании».</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public RowMemory Memory { get; set; } = new();

    public string FullPath { get; }

    public string Name { get; }

    public bool IsDirectory { get; }

    public string Kind => IsDirectory ? "Папка" : "Файл";

    /// <summary>Файл или папка ещё на месте. Удалённое локально из списка убирается.</summary>
    public bool ExistsOnDisk => IsDirectory ? Directory.Exists(FullPath) : File.Exists(FullPath);

    public string SizeText => _sizeBytes < 0 ? "—" : Format(_sizeBytes);

    /// <summary>
    /// Размер числом. По нему сортируется столбец: «1,2 МБ» и «900 Б» как текст
    /// сравниваются по первой цифре, а не по величине. −1 — размер прочитать не удалось.
    /// </summary>
    public long SizeBytes => _sizeBytes;

    /// <summary>Файлов в строке: у файла один, у папки все внутри. −1 — прочитать не удалось.</summary>
    public int Files => _files;

    public string FilesText => _files < 0 ? "—" : _files.ToString();

    /// <summary>Размер того, что пройдёт исключения окна.</summary>
    public long KeptBytes => _keptBytes;

    public string KeptSizeText => _keptBytes < 0 ? "—" : Format(_keptBytes);

    /// <summary>Файлов, которые пройдут исключения окна.</summary>
    public int KeptFiles => _keptFiles;

    public string KeptFilesText => _keptFiles < 0 ? "—" : _keptFiles.ToString();

    public DateTime Modified
    {
        get => _modified;
        private set
        {
            if (Set(ref _modified, value))
            {
                Raise(nameof(ModifiedText));
            }
        }
    }

    public string ModifiedText => Modified == default ? "—" : Modified.ToString("dd.MM.yyyy HH:mm");

    /// <summary>
    /// Перечитывает размер и дату с диска. Файл мог измениться уже после добавления в список,
    /// поэтому таблица обновляет строки при возврате в программу и перед копированием.
    /// Обход папки бывает долгим — вызывать из фонового потока, уведомления сами уйдут в поток интерфейса.
    /// </summary>
    /// <param name="filter">Исключения окна; нет — «Размер*» и «Файлов*» равны полным.</param>
    public void Refresh(ExcludeFilter? filter = null)
    {
        var measured = Measure(filter);

        _sizeBytes = measured.Bytes;
        _files = measured.Files;
        _keptBytes = measured.KeptBytes;
        _keptFiles = measured.KeptFiles;
        RaiseMany(
            nameof(SizeBytes), nameof(SizeText), nameof(Files), nameof(FilesText),
            nameof(KeptBytes), nameof(KeptSizeText), nameof(KeptFiles), nameof(KeptFilesText));
        Modified = measured.Modified;
    }

    /// <summary>
    /// Дата строки для кнопки «Незалитые»: у файла своя, у папки самая свежая среди того, что пройдёт исключения.
    /// Даты вложенных папок тоже считаются: принесённый старый файл своей даты не меняет, а дату папки меняет.
    /// Null — строка вся под исключением или не читается. Обход бывает долгим — вызывать из фонового потока.
    /// </summary>
    public string? ChangeDate(ExcludeFilter filter)
    {
        try
        {
            if (!ExistsOnDisk || filter.Excludes(Name, IsDirectory))
            {
                return null;
            }

            var date = IsDirectory ? Newest(new DirectoryInfo(FullPath), Name, filter) : new FileInfo(FullPath).LastWriteTimeUtc;
            return date.ToString("O");
        }
        catch
        {
            return null;
        }
    }

    private static DateTime Newest(DirectoryInfo dir, string relative, ExcludeFilter filter)
    {
        var newest = dir.LastWriteTimeUtc;

        foreach (var file in dir.EnumerateFiles())
        {
            if (file.LastWriteTimeUtc > newest && !filter.Excludes(relative + "/" + file.Name, isDirectory: false))
            {
                newest = file.LastWriteTimeUtc;
            }
        }

        foreach (var sub in dir.EnumerateDirectories())
        {
            var path = relative + "/" + sub.Name;

            if (!filter.Excludes(path, isDirectory: true))
            {
                var inner = Newest(sub, path, filter);

                if (inner > newest)
                {
                    newest = inner;
                }
            }
        }

        return newest;
    }

    public static string Format(long bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} {units[0]}" : $"{value:0.##} {units[unit]}";
    }

    private (long Bytes, int Files, long KeptBytes, int KeptFiles, DateTime Modified) Measure(ExcludeFilter? filter)
    {
        try
        {
            var excluded = filter is { IsEmpty: false } && filter.Excludes(Name, IsDirectory);

            if (IsDirectory)
            {
                var info = new DirectoryInfo(FullPath);
                var newest = info.LastWriteTime;
                long total = 0;
                var count = 0;

                // дата папки в Windows меняется только от правки её прямых детей,
                // поэтому берём самую свежую дату среди всего содержимого
                foreach (var file in info.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    total += file.Length;
                    count++;

                    if (file.LastWriteTime > newest)
                    {
                        newest = file.LastWriteTime;
                    }
                }

                var (keptBytes, keptFiles) = excluded
                    ? (0L, 0)
                    : filter is { IsEmpty: false } ? Kept(info, Name, filter) : (total, count);

                return (total, count, keptBytes, keptFiles, newest);
            }

            var single = new FileInfo(FullPath);
            return (single.Length, 1, excluded ? 0 : single.Length, excluded ? 0 : 1, single.LastWriteTime);
        }
        catch
        {
            return (-1, -1, -1, -1, default);
        }
    }

    /// <summary>Что пройдёт исключения — тем же обходом, что и план копирования: исключённая папка не обходится.</summary>
    private static (long Bytes, int Files) Kept(DirectoryInfo dir, string relative, ExcludeFilter filter)
    {
        long bytes = 0;
        var files = 0;

        foreach (var file in dir.EnumerateFiles())
        {
            if (!filter.Excludes(relative + "/" + file.Name, isDirectory: false))
            {
                bytes += file.Length;
                files++;
            }
        }

        foreach (var sub in dir.EnumerateDirectories())
        {
            var path = relative + "/" + sub.Name;

            if (!filter.Excludes(path, isDirectory: true))
            {
                var (subBytes, subFiles) = Kept(sub, path, filter);
                bytes += subBytes;
                files += subFiles;
            }
        }

        return (bytes, files);
    }
}
