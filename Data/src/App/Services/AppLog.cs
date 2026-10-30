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

    /// <summary>Раздел, где сделано действие; пусто — сама программа (запуск, обновление).</summary>
    public AppSection? Section { get; init; }

    /// <summary>Запись показывается своей плашкой на месте, а не в углу окна.</summary>
    public bool ShownInPlace { get; init; }

    /// <summary>Отклик интерфейса: только плашка, в журнал не идёт.</summary>
    public bool IsHint { get; init; }

    /// <summary>Строка одного сервера посреди операции: в журнал идёт, плашку не дёргает — итог придёт отдельно.</summary>
    public bool IsServerLine { get; init; }

    public string TimeText => Time.ToString("HH:mm:ss");

    public string SectionText => NameOf(Section);

    /// <summary>Раздел словами, как на кнопке: «Проводник», «Копирование».</summary>
    public static string NameOf(AppSection? section) => section switch
    {
        AppSection.Servers => "Серверы",
        AppSection.Command => "Команда",
        AppSection.Files => "Копирование",
        AppSection.Download => "Скачивание",
        AppSection.Explorer => "Проводник",
        AppSection.Settings => "Настройки",
        _ => "Программа",
    };

    public string ScopeText => Scope ?? string.Empty;

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
/// Журнал действий: что сделано и чем кончилось, с разделом, где это сделано. То же видно
/// в панели внизу окна и в файле UserData\logs\ГГГГ-ММ-ДД.log. Отклик на щелчки по интерфейсу
/// (переключили вид, скопировали путь, «сначала выберите сервер») — <see cref="Hint"/>: плашкой
/// и в файл, а журнал им не засоряется.
/// </summary>
public static class AppLog
{
    private const int MaxEntriesInUi = 5000;

    private static readonly Lock FileSync = new();

    private static readonly AsyncLocal<AppSection?> Ambient = new();

    public static ObservableCollection<LogEntry> Entries { get; } = new();

    public static event Action<LogEntry>? Written;

    /// <summary>
    /// Раздел операции, которая сейчас идёт. Ставится в начале async-метода и действует до его
    /// конца — на всё, что он ждёт: итог долгого копирования попадёт в свой раздел, даже если
    /// человек тем временем ушёл в другой. Не задан — раздел, открытый в окне.
    /// </summary>
    public static AppSection? Section
    {
        get => Ambient.Value;
        set => Ambient.Value = value;
    }

    public static void Info(string text, string? scope = null, AppSection? section = null) => Write(LogLevel.Info, text, scope, section);

    public static void Success(string text, string? scope = null, AppSection? section = null) => Write(LogLevel.Success, text, scope, section);

    public static void Warn(string text, string? scope = null, AppSection? section = null) => Write(LogLevel.Warning, text, scope, section);

    public static void Error(string text, string? scope = null, AppSection? section = null) => Write(LogLevel.Error, text, scope, section);

    public static void Write(LogLevel level, string text, string? scope = null, AppSection? section = null) =>
        Add(new LogEntry { Time = DateTime.Now, Level = level, Text = text, Scope = scope, Section = Resolve(section) });

    /// <summary>
    /// Отклик на щелчок по интерфейсу: плашка в углу и строка в файле, но не журнал.
    /// Журнал — для действий над файлами и серверами, а не для «вид переключён».
    /// </summary>
    public static void Hint(string text, LogLevel level = LogLevel.Info) =>
        Add(new LogEntry { Time = DateTime.Now, Level = level, Text = text, Section = Resolve(null), IsHint = true });

    /// <summary>Итог одного сервера посреди операции на нескольких: в журнал, без плашки.</summary>
    public static void ServerLine(LogLevel level, string text, string scope) =>
        Add(new LogEntry { Time = DateTime.Now, Level = level, Text = text, Scope = scope, Section = Resolve(null), IsServerLine = true });

    /// <summary>
    /// Отладочная запись — только в файл UserData\logs: разбирать по ней, что случилось,
    /// не засоряя журнал в окне и не дёргая плашки на каждый щелчок.
    /// </summary>
    public static void Debug(string text, string? scope = null) =>
        AppendToFile(new LogEntry { Time = DateTime.Now, Level = LogLevel.Debug, Text = text, Scope = scope });

    /// <summary>
    /// Предупреждение, которое показывают плашкой прямо там, где случилось (например, посреди
    /// поля команды). Это отклик интерфейса: в журнал не идёт, угловая плашка его не дублирует.
    /// </summary>
    public static LogEntry WarnInPlace(string text) =>
        Add(new LogEntry { Time = DateTime.Now, Level = LogLevel.Warning, Text = text, Section = Resolve(null), ShownInPlace = true, IsHint = true });

    private static AppSection? Resolve(AppSection? section) =>
        section ?? Ambient.Value ?? (AppState.Current is { TabsLoaded: true } state ? state.Section : null);

    private static LogEntry Add(LogEntry entry)
    {
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
        TerminalStore.MarkDirty();
    }

    /// <summary>Журнал прошлых запусков программы — под записями этого, свежие сверху.</summary>
    public static void Restore(IEnumerable<LogEntry> saved)
    {
        foreach (var entry in saved)
        {
            if (Entries.Count >= MaxEntriesInUi)
            {
                break;
            }

            Entries.Add(entry);
        }
    }

    public static string ExportText()
    {
        var sb = new StringBuilder();

        foreach (var e in Entries)
        {
            sb.AppendLine(FileLine(e));
        }

        return sb.ToString();
    }

    private static string FileLine(LogEntry e) =>
        $"{e.Time:yyyy-MM-dd HH:mm:ss} [{e.Level}] {(e.IsHint ? "(интерфейс) " : string.Empty)}{e.SectionText} · {e.Line}";

    private static void AddToUi(LogEntry entry)
    {
        // свежее — сверху
        if (!entry.IsHint)
        {
            Entries.Insert(0, entry);

            if (Entries.Count > MaxEntriesInUi)
            {
                Entries.RemoveAt(Entries.Count - 1);
            }

            TerminalStore.MarkDirty();
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
                    FileLine(entry) + Environment.NewLine,
                    Encoding.UTF8);
            }
        }
        catch
        {
            // лог не должен ронять приложение
        }
    }
}
