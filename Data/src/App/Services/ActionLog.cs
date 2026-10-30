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

    /// <summary>Условный участник по его Id — для строк терминала, прочитанных после перезапуска.</summary>
    public static ServerEntry? Pseudo(string id) => id switch
    {
        "local:computer" => Computer,
        "local:program" => Program,
        _ => null,
    };

    /// <summary>Кто действует на этой стороне проводника: сервер или компьютер.</summary>
    public static ServerEntry Where(IFileSource? source) => source is RemoteSource remote ? remote.Server : Computer;

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

        if (always || toJournal)
        {
            AppLog.Write(level, summary, ReferenceEquals(where, Program) ? null : where.Display, resolved);
        }
        else
        {
            // журнал выключен — отклик всё равно есть: плашка и файл лога
            AppLog.Hint(summary, level);
        }

        var target = tab ?? Active();

        if (target is null || !(always || toTerminal))
        {
            return;
        }

        var lines = details?.ToList() ?? [];
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
