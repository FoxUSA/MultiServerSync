using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using MultiServerSync.Core;

namespace MultiServerSync.Services;

public enum LogLevel
{
    Info,
    Success,
    Warning,
    Error,

    /// <summary>Отладка: только в файл, в панель «Журнал» и на плашки не попадает.</summary>
    Debug,
}

public sealed class LogEntry
{
    public required DateTime Time { get; init; }

    public required LogLevel Level { get; init; }

    public required string Text { get; init; }

    public string? Scope { get; init; }

    /// <summary>Запись показывается своей плашкой на месте, а не в углу окна.</summary>
    public bool ShownInPlace { get; init; }

    public string TimeText => Time.ToString("HH:mm:ss");

    public string Line => Scope is null ? Text : $"[{Scope}] {Text}";

    public Brush Brush => Level switch
    {
        LogLevel.Success => Brushes.MediumSeaGreen,
        LogLevel.Warning => Brushes.Goldenrod,
        LogLevel.Error => Brushes.IndianRed,
        _ => Brushes.Gainsboro,
    };
}

/// <summary>
/// Единый живой лог: то же самое видно в панели внизу окна и в файле UserData\logs\ГГГГ-ММ-ДД.log.
/// </summary>
public static class AppLog
{
    private const int MaxEntriesInUi = 5000;

    private static readonly Lock FileSync = new();

    public static ObservableCollection<LogEntry> Entries { get; } = new();

    public static event Action<LogEntry>? Written;

    public static void Info(string text, string? scope = null) => Write(LogLevel.Info, text, scope);

    public static void Success(string text, string? scope = null) => Write(LogLevel.Success, text, scope);

    public static void Warn(string text, string? scope = null) => Write(LogLevel.Warning, text, scope);

    public static void Error(string text, string? scope = null) => Write(LogLevel.Error, text, scope);

    /// <summary>
    /// Отладочная запись — только в файл UserData\logs: разбирать по ней, что случилось,
    /// не засоряя журнал в окне и не дёргая плашки на каждый щелчок.
    /// </summary>
    public static void Debug(string text, string? scope = null) =>
        AppendToFile(new LogEntry { Time = DateTime.Now, Level = LogLevel.Debug, Text = text, Scope = scope });

    /// <summary>
    /// Предупреждение, которое показывают плашкой прямо там, где случилось (например, посреди
    /// поля команды). В журнал идёт как обычно, угловая плашка его не дублирует.
    /// </summary>
    public static LogEntry WarnInPlace(string text) => Write(LogLevel.Warning, text, null, inPlace: true);

    public static void Write(LogLevel level, string text, string? scope = null) =>
        Write(level, text, scope, inPlace: false);

    private static LogEntry Write(LogLevel level, string text, string? scope, bool inPlace)
    {
        var entry = new LogEntry
        {
            Time = DateTime.Now,
            Level = level,
            Text = text,
            Scope = scope,
            ShownInPlace = inPlace,
        };

        AppendToFile(entry);

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            AddToUi(entry);
        }
        else
        {
            dispatcher.BeginInvoke(new Action(() => AddToUi(entry)));
        }

        return entry;
    }

    public static void Clear()
    {
        Entries.Clear();
    }

    public static string ExportText()
    {
        var sb = new StringBuilder();

        foreach (var e in Entries)
        {
            sb.AppendLine($"{e.Time:yyyy-MM-dd HH:mm:ss} [{e.Level}] {e.Line}");
        }

        return sb.ToString();
    }

    private static void AddToUi(LogEntry entry)
    {
        // свежее — сверху
        Entries.Insert(0, entry);

        if (Entries.Count > MaxEntriesInUi)
        {
            Entries.RemoveAt(Entries.Count - 1);
        }

        Written?.Invoke(entry);
    }

    private static void AppendToFile(LogEntry entry)
    {
        try
        {
            lock (FileSync)
            {
                File.AppendAllText(
                    AppPaths.LogFileForToday(entry.Time),
                    $"{entry.Time:yyyy-MM-dd HH:mm:ss} [{entry.Level}] {entry.Line}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // лог не должен ронять приложение
        }
    }
}
