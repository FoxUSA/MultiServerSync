using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
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

    /// <summary>Шум вроде «скопировано»: только плашка, и в файл лога тоже не идёт.</summary>
    public bool IsEcho { get; init; }

    /// <summary>Строка одного сервера посреди операции: в журнал идёт, плашку не дёргает — итог придёт отдельно.</summary>
    public bool IsServerLine { get; init; }

    /// <summary>Где лежит полный лог записи — открывается щелчком по строке. Нет — есть только сам текст.</summary>
    public JournalOutputMark? Output { get; init; }

    /// <summary>С датой: журнал переживает перезапуск, и строки разных дней идут подряд.</summary>
    public string TimeText => Time.ToString("dd.MM HH:mm:ss");

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

    /// <summary>Вкладка, где сделано действие: своя пометка или из текста записи («· вкладка «Основная»»).</summary>
    public string? Tab { get; init; }

    private static readonly Regex TabInText = new("· вкладка «(.+?)»", RegexOptions.Compiled);

    private string? _tabText;

    /// <summary>Название вкладки записи; пусто — запись не от вкладки.</summary>
    public string TabText => _tabText ??= Tab ?? TabIn(Text) ?? string.Empty;

    /// <summary>Вкладка из текста вида «Копирование · вкладка «Основная»».</summary>
    public static string? TabIn(string text) => TabInText.Match(text) is { Success: true } found ? found.Groups[1].Value : null;

    private string? _action;

    /// <summary>Столбец «Действие»: копирование, команда, удаление файла…</summary>
    public string ActionText => _action ??= JournalAction.Of(Text);

    /// <summary>Шапка лога записи: сервер, раздел, вкладка — как в выводе терминала.</summary>
    public string PlaceText
    {
        get
        {
            var parts = new List<string>();

            if (Scope is { } scope)
            {
                // «Компьютер» и «3 сервер(ов)» — не имя сервера
                parts.Add(scope == ActionLog.Computer.Name || scope.EndsWith("сервер(ов)", StringComparison.Ordinal) ? scope : "Сервер: " + scope);
            }

            parts.Add("Раздел: " + SectionText);

            if (TabText is { Length: > 0 } tab)
            {
                parts.Add("Вкладка: " + tab);
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>По чему ищет строка поиска журнала: всё, что видно в строке таблицы, и сервер записи.</summary>
    public string SearchText => $"{TimeText} {SectionText} {ActionText} {ScopeText} {Text}";

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

    public static void Info(string text, string? scope = null, AppSection? section = null, string? details = null) => Write(LogLevel.Info, text, scope, section, details);

    public static void Success(string text, string? scope = null, AppSection? section = null) => Write(LogLevel.Success, text, scope, section);

    public static void Warn(string text, string? scope = null, AppSection? section = null) => Write(LogLevel.Warning, text, scope, section);

    public static void Error(string text, string? scope = null, AppSection? section = null) => Write(LogLevel.Error, text, scope, section);

    /// <param name="details">Полный лог записи: показывается щелчком по строке журнала.</param>
    /// <param name="tab">Вкладка, если её нет в самом тексте записи.</param>
    public static void Write(LogLevel level, string text, string? scope = null, AppSection? section = null, string? details = null, string? tab = null)
    {
        var time = DateTime.Now;
        var resolved = Resolve(section);

        Add(new LogEntry { Time = time, Level = level, Text = text, Scope = scope, Section = resolved, Tab = tab, Output = SaveDetails(time, text, scope, resolved, details) });
    }

    /// <summary>
    /// Отклик на щелчок по интерфейсу: плашка в углу и строка в файле, но не журнал.
    /// Журнал — для действий над файлами и серверами, а не для «вид переключён».
    /// </summary>
    public static void Hint(string text, LogLevel level = LogLevel.Info) =>
        Add(new LogEntry { Time = DateTime.Now, Level = level, Text = text, Section = Resolve(null), IsHint = true });

    /// <summary>
    /// «Скопировано», «вставлено»: человеку видно, что нажатие сработало, а в логах это мусор —
    /// ни в журнал, ни в терминал, ни в файл, только плашка.
    /// </summary>
    public static void Echo(string text, LogLevel level = LogLevel.Info) =>
        Add(new LogEntry { Time = DateTime.Now, Level = level, Text = text, Section = Resolve(null), IsHint = true, IsEcho = true });

    /// <summary>Запись самой программы, а не раздела: обновление, сбой. Открытый раздел тут ни при чём, в столбце — «Программа».</summary>
    public static void General(LogLevel level, string text) =>
        Add(new LogEntry { Time = DateTime.Now, Level = level, Text = text });

    /// <summary>
    /// Запуск операции — только строка в файле дня. Журнал и плашка получат её итог, а по файлу видно,
    /// что запуск был, даже если итога не дождались (программу закрыли посреди команды).
    /// </summary>
    public static void Started(string text, string? scope = null) =>
        AppendToFile(new LogEntry { Time = DateTime.Now, Level = LogLevel.Info, Text = text, Scope = scope, Section = Resolve(null) });

    /// <summary>Итог одного сервера посреди операции на нескольких: в журнал, без плашки.</summary>
    public static void ServerLine(LogLevel level, string text, string scope, string? details = null)
    {
        var time = DateTime.Now;
        var resolved = Resolve(null);

        Add(new LogEntry { Time = time, Level = level, Text = text, Scope = scope, Section = resolved, IsServerLine = true, Output = SaveDetails(time, text, scope, resolved, details) });
    }

    private static JournalOutputMark? SaveDetails(DateTime time, string text, string? scope, AppSection? section, string? details) =>
        details is null
            ? null
            : JournalOutput.Save(time, $"{LogEntry.NameOf(section)} · {(scope is null ? text : $"[{scope}] {text}")}", details);

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

    /// <summary>Итог, который человек уже видит в открытом окне: в журнал идёт, плашка главного окна его не повторяет.</summary>
    public static void InPlace(LogLevel level, string text, string? scope = null) =>
        Add(new LogEntry { Time = DateTime.Now, Level = level, Text = text, Scope = scope, Section = Resolve(null), ShownInPlace = true });

    private static AppSection? Resolve(AppSection? section) =>
        section ?? Ambient.Value ?? (AppState.Current is { TabsLoaded: true } state ? state.Section : null);

    private static LogEntry Add(LogEntry entry)
    {
        if (!entry.IsEcho)
        {
            AppendToFile(entry);
        }

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
        JournalOutput.DeleteAll();
        TerminalStore.MarkDirty();
    }

    /// <summary>Журнал прошлых запусков программы — под записями этого, свежие сверху.</summary>
    public static void Restore(IEnumerable<LogEntry> saved)
    {
        // шум, записанный прежними версиями, обратно не встаёт
        foreach (var entry in saved.Where(e => !JournalAction.IsNoise(e.Text)))
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
