using System.Windows;
using MultiServerSync.Models;

namespace MultiServerSync.Services;

/// <summary>
/// Разовое действие — и в журнал, и строкой в терминал открытой вкладки, с полным выводом, как у
/// копирования: создали папку, переименовали, убрали строку из списка, сменили исключения.
/// Терминал считает строки по серверам, поэтому у действий без сервера свои участники:
/// «Компьютер» — файлы на этом ПК, «Программа» — списки, вкладки, наборы и настройки.
/// </summary>
public static class ActionLog
{
    public static readonly ServerEntry Computer = new() { Id = "local:computer", Name = "Компьютер", Host = string.Empty };

    public static readonly ServerEntry Program = new() { Id = "local:program", Name = "Программа", Host = string.Empty };

    /// <summary>«PowerShell» на «Команде»: команды идут на этот компьютер, а не на серверы.</summary>
    public static readonly ServerEntry LocalPc = new() { Id = ServerEntry.LocalPcId, Name = "PowerShell", Host = string.Empty };

    /// <summary>Условный участник по его Id — для строк терминала, прочитанных после перезапуска.</summary>
    public static ServerEntry? Pseudo(string id) => id switch
    {
        "local:computer" => Computer,
        "local:program" => Program,
        ServerEntry.LocalPcId => LocalPc,
        _ => null,
    };

    /// <summary>Кто действует на этой стороне проводника: сервер или компьютер.</summary>
    public static ServerEntry Where(IFileSource? source) => source is RemoteSource remote ? remote.Server : Computer;

    /// <summary>Столбец «Где» журнала: сервер, а если их несколько — их число; нет серверов — пусто.</summary>
    public static string? WhereText(IEnumerable<ServerEntry> servers)
    {
        var distinct = servers.DistinctBy(s => s.Id).ToList();

        return distinct.Count switch
        {
            0 => null,
            1 => distinct[0].Display,
            _ => $"{distinct.Count} сервер(ов)",
        };
    }

    public static void Success(string summary, ServerEntry? where = null, AppSection? section = null, Workspace? tab = null, IEnumerable<string>? details = null) =>
        Write(LogLevel.Success, summary, where, section, tab, details);

    public static void Info(string summary, ServerEntry? where = null, AppSection? section = null, Workspace? tab = null, IEnumerable<string>? details = null) =>
        Write(LogLevel.Info, summary, where, section, tab, details);

    public static void Warn(string summary, ServerEntry? where = null, AppSection? section = null, Workspace? tab = null, IEnumerable<string>? details = null) =>
        Write(LogLevel.Warning, summary, where, section, tab, details);

    public static void Error(string summary, ServerEntry? where = null, AppSection? section = null, Workspace? tab = null, IEnumerable<string>? details = null) =>
        Write(LogLevel.Error, summary, where, section, tab, details);

    /// <param name="where">Сервер; нет — «Программа».</param>
    /// <param name="section">Раздел записи; нет — раздел идущей операции или открытый в окне.</param>
    /// <param name="tab">Вкладка, в чей терминал ложится строка; нет — открытая.</param>
    /// <param name="details">Полный вывод строки: что именно сделано, по строке на объект.</param>
    public static void Write(
        LogLevel level,
        string summary,
        ServerEntry? where = null,
        AppSection? section = null,
        Workspace? tab = null,
        IEnumerable<string>? details = null)
    {
        where ??= Program;

        // шестерёнка у «Терминал / Журнал»: что писать — по разделу действия; ошибки и оговорки — всегда
        var resolved = section ?? AppLog.Section ?? AppState.Current.Section;
        var prefs = AppState.Current.ActionOutputOf(resolved);
        var always = level is LogLevel.Error or LogLevel.Warning;
        var (toTerminal, toJournal) = ReferenceEquals(where, Computer) ? (prefs.ComputerTerminal, prefs.ComputerJournal)
            : ReferenceEquals(where, Program) ? (prefs.ProgramTerminal, prefs.ProgramJournal)
            : (prefs.ServerTerminal, prefs.ServerJournal);
        var lines = details?.ToList() ?? [];
        var target = tab ?? Active();

        // у ошибок «Проводника» нет прогона с итогом: красная рамка на ярлыке и кнопке раздела — отсюда
        if (level == LogLevel.Error && resolved == AppSection.Explorer && AppState.Current is { TabsLoaded: true } state)
        {
            var failed = tab ?? state.ActiveExplorerTab;

            if (failed.Status != WorkspaceStatus.Running)
            {
                failed.ShowStatus("ошибка", string.Empty, WorkspaceStatus.Failed, summary);
            }
        }

        if (always || toJournal)
        {
            // в шапку записи — вкладка раздела действия: открытая может быть из другого раздела
            var tabName = target is not null && resolved != AppSection.Servers && AppState.Current.SectionOfTab(target) == resolved
                ? target.Title
                : null;

            AppLog.Write(level, summary, ReferenceEquals(where, Program) ? null : where.Display, resolved,
                lines.Count > 0 ? string.Join(Environment.NewLine, lines) : null, tabName);
        }
        else if (ReferenceEquals(where, Program))
        {
            // действия программы человеку не нужны: только файл лога, со стёртым — на случай разбора
            AppLog.Debug(lines.Count > 0 ? summary + Environment.NewLine + string.Join(Environment.NewLine, lines) : summary);
        }
        else
        {
            // журнал выключен — отклик всё равно есть: плашка и файл лога
            AppLog.Hint(summary, level);
        }

        if (target is null || !(always || toTerminal))
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;

        // строки терминала живут в окне — из рабочего потока только через него
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => target.AddActionRow(where, level, summary, lines));
            return;
        }

        target.AddActionRow(where, level, summary, lines);
    }

    /// <summary>Открытая вкладка — пока вкладки не загружены, строке некуда лечь.</summary>
    private static Workspace? Active() =>
        AppState.Current is { TabsLoaded: true } state && state.Tabs.Count > 0 ? state.TerminalOwner : null;
}
