using System.IO;
using System.Text;
using MultiServerSync.Core;

namespace MultiServerSync.Services;

/// <summary>Где в файлах лежит полный лог записи журнала.</summary>
public sealed class JournalOutputMark
{
    public string Part { get; set; } = string.Empty;

    public long Offset { get; set; }

    public int Length { get; set; }
}

/// <summary>
/// Полный лог записей журнала: UserData\logs\output, обычные текстовые файлы, только дописываются.
/// В journal.json лежит лишь место записи — он переписывается каждые несколько секунд, а вывод
/// большого копирования занимает мегабайты. Читается по щелчку на строку журнала.
/// </summary>
public static class JournalOutput
{
    /// <summary>
    /// Длиннее — вырезается середина: поле вывода умирает от числа строк, а не от мегабайт —
    /// миллион коротких строк весит всего около 10 МБ.
    /// </summary>
    private const int MaxLines = 20000;

    private const long Megabyte = 1024L * 1024;

    private static readonly UTF8Encoding Utf8 = new(false);

    private static readonly Lock Sync = new();

    private static string? _part;

    private static long _sinceTrim;

    private static string Dir => Path.Combine(AppPaths.LogsDir, "output");

    private static long LimitBytes => AppState.Current.Settings.LogsMaxMb * Megabyte;

    /// <summary>
    /// Старое удаляется целыми файлами, а файл, куда идёт запись, не трогается. Поэтому файл —
    /// пятая часть предела: при 10 МБ по 2 МБ, и в предел входят несколько файлов.
    /// </summary>
    private static long PartBytes => LimitBytes / 5;

    /// <summary>Дописать лог записи. Пустой или несохранившийся — null: запись останется без него.</summary>
    /// <param name="title">Заголовок блока в файле — чтобы файл читался и без программы.</param>
    public static JournalOutputMark? Save(DateTime time, string title, string text)
    {
        text = text.TrimEnd();

        if (text.Length == 0)
        {
            return null;
        }

        var head = Utf8.GetBytes($"{Environment.NewLine}═══ {time:dd.MM.yyyy HH:mm:ss} · {title} ═══{Environment.NewLine}");

        // один лог не вытесняет остальные: не больше файла, кириллица — два байта на символ
        var body = Utf8.GetBytes(LogTrim.Chars(LogTrim.Lines(text, MaxLines), (int)(PartBytes / 2)) + Environment.NewLine);
        JournalOutputMark mark;
        bool trim;

        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Dir);
                var part = PartFor(time);

                using var file = new FileStream(Path.Combine(Dir, part), FileMode.Append, FileAccess.Write, FileShare.Read);
                mark = new JournalOutputMark { Part = part, Offset = file.Length + head.Length, Length = body.Length };
                file.Write(head);
                file.Write(body);

                _sinceTrim += head.Length + body.Length;
                trim = _sinceTrim >= PartBytes / 2;
            }
        }
        catch
        {
            // лог не должен ронять приложение
            return null;
        }

        if (trim)
        {
            Task.Run(Trim);
        }

        return mark;
    }

    /// <summary>Текст лога; null — файла уже нет.</summary>
    public static string? Read(JournalOutputMark mark)
    {
        try
        {
            var path = Path.Combine(Dir, mark.Part);

            if (!File.Exists(path))
            {
                return null;
            }

            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            if (mark.Offset + mark.Length > file.Length)
            {
                return null;
            }

            var bytes = new byte[mark.Length];
            file.Seek(mark.Offset, SeekOrigin.Begin);
            file.ReadExactly(bytes);
            return Utf8.GetString(bytes);
        }
        catch (Exception ex)
        {
            AppLog.Debug("Не удалось прочитать лог записи журнала: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Держит папку логов в пределе из настроек: файлы дней и полный вывод, старые удаляются первыми.
    /// Файлы, в которые идёт запись, остаются.
    /// </summary>
    public static void Trim()
    {
        var limit = LimitBytes;

        lock (Sync)
        {
            _sinceTrim = 0;

            try
            {
                var today = AppPaths.LogFileForToday(DateTime.Now);
                var current = _part is null ? null : Path.Combine(Dir, _part);

                var files = new DirectoryInfo(AppPaths.LogsDir)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Where(f => f.Extension is ".log" or ".txt")
                    .OrderBy(f => f.LastWriteTimeUtc)
                    .ToList();

                var total = files.Sum(f => f.Length);

                foreach (var file in files)
                {
                    if (total <= limit)
                    {
                        break;
                    }

                    if (string.Equals(file.FullName, today, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(file.FullName, current, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        var length = file.Length;
                        file.Delete();
                        total -= length;
                    }
                    catch (Exception ex)
                    {
                        AppLog.Debug($"Не удалось удалить старый лог {file.Name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Debug("Не удалось проверить размер логов: " + ex.Message);
            }
        }
    }

    /// <summary>«Очистить» в журнале: записей нет — их полный вывод тоже не нужен. Файлы дней остаются.</summary>
    public static void DeleteAll()
    {
        lock (Sync)
        {
            _part = null;
            _sinceTrim = 0;

            try
            {
                if (!Directory.Exists(Dir))
                {
                    return;
                }

                foreach (var path in Directory.EnumerateFiles(Dir, "*.txt"))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                AppLog.Debug("Не удалось удалить вывод журнала: " + ex.Message);
            }
        }
    }

    /// <summary>Файл для записи: последний сегодняшний, пока не дорос до предела, иначе новый.</summary>
    private static string PartFor(DateTime time)
    {
        _part ??= Directory.EnumerateFiles(Dir, "*.txt")
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .LastOrDefault();

        var day = time.ToString("yyyy-MM-dd");

        if (_part is null
            || !_part.StartsWith(day, StringComparison.Ordinal)
            || (File.Exists(Path.Combine(Dir, _part)) && new FileInfo(Path.Combine(Dir, _part)).Length >= PartBytes))
        {
            _part = $"{time:yyyy-MM-dd_HH-mm-ss}.txt";
        }

        return _part;
    }
}
