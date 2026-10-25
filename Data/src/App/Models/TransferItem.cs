using System.IO;
using MultiServerSync.Core;

namespace MultiServerSync.Models;

/// <summary>Локальный файл или папка, выбранные для отправки на серверы.</summary>
public sealed class TransferItem : ObservableObject
{
    private bool _isSelected = true;
    private string _sizeText = "—";
    private long _sizeBytes = -1;
    private DateTime _modified;

    public TransferItem(string path)
    {
        FullPath = path;
        IsDirectory = Directory.Exists(path);
        Name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));

        if (string.IsNullOrEmpty(Name))
        {
            Name = path;
        }

        Refresh();
    }

    /// <summary>Галочка «этот объект участвует в копировании».</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public string FullPath { get; }

    public string Name { get; }

    public bool IsDirectory { get; }

    public string Kind => IsDirectory ? "Папка" : "Файл";

    /// <summary>Файл или папка ещё на месте. Удалённое локально из списка убирается.</summary>
    public bool ExistsOnDisk => IsDirectory ? Directory.Exists(FullPath) : File.Exists(FullPath);

    public string SizeText
    {
        get => _sizeText;
        private set => Set(ref _sizeText, value);
    }

    /// <summary>
    /// Размер числом. По нему сортируется столбец: «1,2 МБ» и «900 Б» как текст
    /// сравниваются по первой цифре, а не по величине. −1 — размер прочитать не удалось.
    /// </summary>
    public long SizeBytes
    {
        get => _sizeBytes;
        private set => Set(ref _sizeBytes, value);
    }

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
    public void Refresh()
    {
        var (size, bytes, modified) = Measure();

        SizeText = size;
        SizeBytes = bytes;
        Modified = modified;
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

    private (string Size, long Bytes, DateTime Modified) Measure()
    {
        try
        {
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

                return ($"{count} файл(ов), {Format(total)}", total, newest);
            }

            var single = new FileInfo(FullPath);
            return (Format(single.Length), single.Length, single.LastWriteTime);
        }
        catch
        {
            return ("—", -1, default);
        }
    }
}
