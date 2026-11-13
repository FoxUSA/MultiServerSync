using System.Collections.ObjectModel;
using MultiServerSync.Models;

namespace MultiServerSync.Services;

/// <summary>
/// Избранные команды по вкладкам. Записи и вкладки общие для всех разделов; вкладка — просто
/// название-папка, запись лежит ровно в одной. Каждое изменение сразу пишется в settings.json и в журнал.
/// </summary>
public static class CommandBook
{
    /// <summary>Первая вкладка, когда их ещё нет: сюда попадают записи старых версий.</summary>
    public const string DefaultGroup = "Общее";

    private static AppSettings Settings => AppState.Current.Settings;

    /// <summary>«PowerShell» на «Команде» — своё избранное: команды для этого компьютера с серверными не мешаются.</summary>
    private static bool Pc => AppState.Current.CommandLocalPc;

    public static ObservableCollection<CommandSnippet> Snippets => Pc ? Settings.LocalCommandBook : Settings.CommandBook;

    public static ObservableCollection<string> Groups => Pc ? Settings.LocalCommandBookGroups : Settings.CommandBookGroups;

    /// <summary>Вкладок нет — появляется «Общее»; запись без вкладки уходит в первую, с незнакомой — вкладка заводится.</summary>
    public static void Normalize()
    {
        var changed = false;

        if (Groups.Count == 0)
        {
            Groups.Add(DefaultGroup);
            changed = true;
        }

        foreach (var snippet in Snippets)
        {
            if (Groups.Contains(snippet.Group))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(snippet.Group))
            {
                snippet.Group = Groups[0];
            }
            else
            {
                Groups.Add(snippet.Group);
            }

            changed = true;
        }

        if (changed)
        {
            AppState.Current.SaveSettings();
        }
    }

    public static List<CommandSnippet> In(string group) => Snippets.Where(s => s.Group == group).ToList();

    private static string? GroupNamed(string name) =>
        Groups.FirstOrDefault(g => string.Equals(g, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Новая вкладка в конец. Возвращает причину отказа или null.</summary>
    public static string? AddGroup(string name, AppSection section, Workspace? tab)
    {
        if (GroupNamed(name) is { } clash)
        {
            return $"Вкладка «{clash}» уже есть.";
        }

        Groups.Add(name);
        AppState.Current.SaveSettings();
        ActionLog.Info($"Добавлена вкладка избранного «{name}»", null, section, tab);
        return null;
    }

    /// <summary>Переименовать вкладку вместе с её записями. Возвращает причину отказа или null.</summary>
    public static string? RenameGroup(string old, string name, AppSection section, Workspace? tab)
    {
        if (old == name)
        {
            return null;
        }

        if (GroupNamed(name) is { } clash && clash != old)
        {
            return $"Вкладка «{clash}» уже есть.";
        }

        var index = Groups.IndexOf(old);

        if (index < 0)
        {
            return null;
        }

        foreach (var snippet in In(old))
        {
            snippet.Group = name;
        }

        Groups[index] = name;
        AppState.Current.SaveSettings();

        // вкладки программы, которые помнят старое название, остаются на своём месте
        foreach (var workspace in AppState.Current.AllWorkspaces.Where(w => w.BookGroup == old && w.LocalPc == Pc))
        {
            workspace.SetBookPlace(name, workspace.BookScroll, workspace.BookHistory);
        }
        ActionLog.Info($"Переименована вкладка избранного: «{old}» → «{name}»", null, section, tab);
        return null;
    }

    /// <summary>Удалить вкладку вместе с записями. Последняя вкладка не удаляется — возвращается причина.</summary>
    public static string? RemoveGroup(string name, AppSection section, Workspace? tab)
    {
        if (Groups.Count < 2)
        {
            return "Это единственная вкладка избранного — её можно переименовать, но не удалить.";
        }

        var gone = In(name);

        foreach (var snippet in gone)
        {
            Snippets.Remove(snippet);
        }

        Groups.Remove(name);
        AppState.Current.SaveSettings();
        ActionLog.Info($"Убрана вкладка избранного «{name}» вместе с записями: {gone.Count}", null, section, tab,
            gone.Select(s => s.Comment));
        return null;
    }

    public static void MoveGroup(string name, int to)
    {
        var from = Groups.IndexOf(name);

        if (from < 0 || from == to)
        {
            return;
        }

        Groups.Move(from, Math.Clamp(to, 0, Groups.Count - 1));
        AppState.Current.SaveSettings();
    }

    /// <summary>Переставить запись внутри её вкладки: записи других вкладок остаются на своих местах.</summary>
    public static void MoveSnippet(CommandSnippet snippet, int to)
    {
        var order = In(snippet.Group);
        var from = order.IndexOf(snippet);

        if (from < 0 || from == to)
        {
            return;
        }

        // места записей этой вкладки в общем списке не меняются — меняется, кто на них стоит
        var slots = order.Select(s => Snippets.IndexOf(s)).ToList();
        order.RemoveAt(from);
        order.Insert(Math.Clamp(to, 0, order.Count), snippet);

        for (var i = 0; i < slots.Count; i++)
        {
            var at = Snippets.IndexOf(order[i]);

            if (at != slots[i])
            {
                Snippets.Move(at, slots[i]);
            }
        }

        AppState.Current.SaveSettings();
    }

    /// <summary>Запись брошена на другую вкладку: уходит в её конец. Возвращает причину отказа или null.</summary>
    public static string? MoveToGroup(CommandSnippet snippet, string group, AppSection section, Workspace? tab)
    {
        if (snippet.Group == group)
        {
            return null;
        }

        if (Snippets.FirstOrDefault(s => s.Group == group
                && string.Equals(s.Comment, snippet.Comment, StringComparison.OrdinalIgnoreCase)) is { } clash)
        {
            return $"Во вкладке «{group}» уже есть запись «{clash.Comment}» — сначала переименуйте одну из них.";
        }

        var from = snippet.Group;
        snippet.Group = group;
        Snippets.Move(Snippets.IndexOf(snippet), Snippets.Count - 1);
        AppState.Current.SaveSettings();
        ActionLog.Info($"Запись избранного перенесена: «{from}» → «{group}»: {snippet.Comment}", null, section, tab);
        return null;
    }

    /// <summary>
    /// Записать новую запись (<paramref name="target"/> = null) или изменить старую. Название уникально
    /// внутри вкладки. Возвращает причину отказа или null; записанная запись — в <paramref name="saved"/>.
    /// </summary>
    public static string? Save(CommandSnippet? target, string name, string group, IReadOnlyList<SavedCommandStep> steps,
        AppSection section, Workspace? tab, out CommandSnippet? saved)
    {
        saved = null;

        if (name.Length == 0)
        {
            return "У записи должно быть название.";
        }

        if (steps.Count == 0)
        {
            return "Команда пустая — записывать нечего.";
        }

        if (Snippets.FirstOrDefault(s => !ReferenceEquals(s, target) && s.Group == group
                && string.Equals(s.Comment, name, StringComparison.OrdinalIgnoreCase)) is { } clash)
        {
            return $"Во вкладке «{group}» уже есть запись «{clash.Comment}» — выберите другое название.";
        }

        var lines = steps.Select(s => OneLine(s.Text));

        if (target is null)
        {
            target = new CommandSnippet { Comment = name, Group = group };
            target.SetSteps(steps);
            Snippets.Add(target);
            ActionLog.Info(steps.Count > 1 ? $"В избранное «{group}» добавлен набор из {steps.Count} окон: {name}" : $"В избранное «{group}» добавлено: {name}",
                null, section, tab, lines);
        }
        else
        {
            var moved = target.Group != group;

            target.Comment = name;
            target.SetSteps(steps);

            // в другую вкладку запись уходит в её конец
            if (moved)
            {
                target.Group = group;
                Snippets.Move(Snippets.IndexOf(target), Snippets.Count - 1);
            }

            ActionLog.Info(moved ? $"Запись избранного изменена и перенесена во вкладку «{group}»: {name}" : $"Запись избранного изменена: {name}",
                null, section, tab, lines);
        }

        AppState.Current.SaveSettings();
        saved = target;
        return null;
    }

    public static void Remove(CommandSnippet snippet, AppSection section, Workspace? tab)
    {
        if (!Snippets.Remove(snippet))
        {
            return;
        }

        AppState.Current.SaveSettings();
        ActionLog.Info($"Из избранного удалено: {snippet.Comment}", null, section, tab,
            snippet.AsSteps().Select(s => s.Text.TrimEnd()));
    }

    private static string OneLine(string text) => Views.Pages.CommandPage.OneLine(text);
}
