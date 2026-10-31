using System.Text.Json;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using MultiServerSync.Models;

namespace MultiServerSync.Services;

/// <summary>Что вкладка умеет вернуть: окна со списками, путями и параметрами, команды. Название — для сверки: его отмена не здесь.</summary>
public sealed class TabContent(string title, List<SavedCopyWindow> windows, int focused, List<SavedCommandStep> steps, bool showNames)
{
    public string Title { get; } = title;

    public List<SavedCopyWindow> Windows { get; } = windows;

    public int Focused { get; } = focused;

    public List<SavedCommandStep> Steps { get; } = steps;

    public bool ShowNames { get; } = showNames;

    /// <summary>Для сравнения. Размеры и окно в фокусе не считаются: само по себе это не действие.</summary>
    public string Signature { get; } = Sign(windows, steps, showNames, marks: true);

    /// <summary>То же без галочек: они одни разрешены под замком.</summary>
    public string Shape { get; } = Sign(windows, steps, showNames, marks: false);

    /// <summary>То же и без порядка и названий окон и без команд: их, как и галочки, можно менять под замком.</summary>
    public string Content { get; } = Sign(
        windows.OrderBy(w => Sign([w], [], false, marks: false, names: false), StringComparer.Ordinal).ToList(), [], showNames, marks: false, names: false);

    private static string Sign(List<SavedCopyWindow> windows, List<SavedCommandStep> steps, bool showNames, bool marks, bool names = true) =>
        JsonSerializer.Serialize(new
        {
            showNames = names && showNames,
            Windows = windows.Select(w => new
            {
                Items = w.CopyList.Select(i => $"{i.Path}|{marks && i.IsSelected}"),
                Downloads = (w.Downloads ?? []).Select(d => $"{d.ServerId}|{d.Path}|{marks && d.IsSelected}"),
                w.RemotePath,
                w.LocalFolder,
                Name = names ? w.Name : null,
                w.OverwriteFiles,
                w.PreserveTimestamps,
                w.VerifyChecksums,
                w.MatchByDate,
                w.MatchByHash,
                w.ExcludeMasks,
                w.UseGlobalExcludes,
            }),
            Steps = steps.Select(s => $"{s.UseSudo}|{s.Text}"),
        });

    /// <summary>Что поменялось — словами для подсказки и журнала.</summary>
    public static string Describe(TabContent before, TabContent after)
    {
        static int Lines(TabContent c) => c.Windows.Sum(w => w.CopyList.Count + (w.Downloads?.Count ?? 0));

        if (before.Windows.Count != after.Windows.Count)
        {
            return after.Windows.Count > before.Windows.Count ? "добавление окна" : "удаление окна";
        }

        // те же окна в другом порядке
        var was = before.Windows.Select(w => JsonSerializer.Serialize(w)).ToList();
        var now = after.Windows.Select(w => JsonSerializer.Serialize(w)).ToList();

        if (!was.SequenceEqual(now) && was.Order().SequenceEqual(now.Order()))
        {
            return "порядок окон";
        }

        if (Lines(before) != Lines(after))
        {
            return Lines(after) > Lines(before) ? "добавление строк" : "удаление строк";
        }

        if (before.Steps.Count != after.Steps.Count)
        {
            return after.Steps.Count > before.Steps.Count ? "добавление окна команды" : "удаление окна команды";
        }

        if (before.Shape == after.Shape)
        {
            return "галочки";
        }

        if (before.ShowNames != after.ShowNames
            || !before.Windows.Select(w => w.Name).SequenceEqual(after.Windows.Select(w => w.Name)))
        {
            return "название окна";
        }

        if (!before.Steps.Select(s => s.Text).SequenceEqual(after.Steps.Select(s => s.Text)))
        {
            return "правка команды";
        }

        var paths = before.Windows.Zip(after.Windows).Any(p =>
            p.First.RemotePath != p.Second.RemotePath
            || p.First.LocalFolder != p.Second.LocalFolder
            || !p.First.CopyList.Select(i => i.Path).SequenceEqual(p.Second.CopyList.Select(i => i.Path))
            || !(p.First.Downloads ?? []).Select(d => d.Path).SequenceEqual((p.Second.Downloads ?? []).Select(d => d.Path)));

        return paths ? "правка пути" : "параметры";
    }
}

/// <summary>Список серверов и наборов: сами объекты и их поля на тот момент.</summary>
public sealed class ServersContent(List<(ServerEntry Entry, ServerEntry Fields)> servers, List<(ServerSet Set, string Title, bool Archived)> sets)
{
    public List<(ServerEntry Entry, ServerEntry Fields)> Servers { get; } = servers;

    public List<(ServerSet Set, string Title, bool Archived)> Sets { get; } = sets;

    public string Signature { get; } = string.Join('\n',
        servers.Select(s => s.Fields.Signature()).Concat(sets.Select(s => $"{s.Set.Id}|{s.Title}|{s.Archived}")));

    /// <summary>Набор закрыт или переименован — такой шаг идёт на значок в ряду разделов, как и у вкладок.</summary>
    public static string? TabAction(ServersContent before, ServersContent after)
    {
        foreach (var (set, title, _) in before.Sets)
        {
            var now = after.Sets.FirstOrDefault(s => ReferenceEquals(s.Set, set));

            if (now.Set is null)
            {
                return $"закрытие набора «{title}»";
            }

            if (now.Title != title)
            {
                return $"переименование набора «{title}»";
            }
        }

        return null;
    }

    public static string Describe(ServersContent before, ServersContent after)
    {
        if (after.Sets.Count > before.Sets.Count)
        {
            return "добавление набора";
        }

        if (before.Servers.Count != after.Servers.Count)
        {
            return after.Servers.Count > before.Servers.Count ? "добавление серверов" : "удаление серверов";
        }

        return "правка серверов";
    }
}

/// <summary>Где стояла закрытая вкладка: сама вкладка жива, её можно поставить обратно целиком.</summary>
public sealed record TabPlace(Workspace Tab, AppSection Section, int Index, Workspace? Personal, int PersonalIndex, Workspace? Owner, bool OwnerEnabled);

/// <summary>
/// Отмена и возврат действий: своя память у каждой вкладки (на «Серверах» — у набора), кроме «Проводника».
/// Действия ничего не сообщают сами: после каждого сохранения вкладки сверяются с тем, что было, и разница
/// становится шагом. Так новое действие попадает сюда без правок, а запуск команд и копирование содержимое
/// вкладки не меняют и шагов не дают. Живёт до закрытия программы; у закрытой вкладки память остаётся и
/// возвращается вместе с ней. Закрытие и переименование самих вкладок — отдельно, по разделам: стрелки
/// вкладки их не касаются, для них значок в ряду разделов, он только отменяет.
/// </summary>
public static class UndoJournal
{
    private const int Depth = 100;

    /// <summary>Набор текста — один шаг, пока между буквами меньше этого.</summary>
    private static readonly TimeSpan TypingGap = TimeSpan.FromSeconds(2);

    private abstract class Step
    {
        public string What { get; set; } = string.Empty;

        /// <summary>Можно ли применить сейчас; нельзя — причина уже показана.</summary>
        public abstract bool Ready();

        /// <summary>false — не вышло, причина показана, шаг остаётся на месте.</summary>
        public abstract bool Apply(bool undo);
    }

    private sealed class ContentStep(Workspace tab, TabContent before, TabContent after, bool typing) : Step
    {
        public Workspace Tab { get; } = tab;

        public TabContent Before { get; } = before;

        public TabContent After { get; set; } = after;

        public bool Typing { get; } = typing;

        public DateTime At { get; set; } = DateTime.UtcNow;

        public override bool Ready()
        {
            if (Tab.IsBusy)
            {
                AppLog.Hint($"Вкладка «{Tab.Title}» занята операцией — сначала дождитесь её окончания.", LogLevel.Warning);
                return false;
            }

            if (Tab.HeldSteps is not null)
            {
                AppLog.Hint("Идёт правка записи избранного — сначала закончите её.", LogLevel.Warning);
                return false;
            }

            // галочки у строк, порядок и названия окон замок разрешает, остальное — нет
            return !Tab.IsContentLocked || Before.Content == After.Content || !Tab.BlockedByLock();
        }

        public override bool Apply(bool undo)
        {
            Tab.ApplyContent(undo ? Before : After);
            return true;
        }
    }

    private sealed class ServersStep(ServersContent before, ServersContent after) : Step
    {
        public override bool Ready()
        {
            if (AppState.Current.AllWorkspaces.Any(w => w.IsBusy))
            {
                AppLog.Hint("Идёт операция — дождитесь её окончания.", LogLevel.Warning);
                return false;
            }

            return true;
        }

        public override bool Apply(bool undo)
        {
            AppState.Current.ApplyServers(undo ? after : before, undo ? before : after);
            return true;
        }
    }

    /// <summary>Вкладку добавили или закрыли: отмена и возврат ставят её и убирают.</summary>
    private sealed class TabStep(TabPlace place, bool added) : Step
    {
        public override bool Ready() => true;

        public override bool Apply(bool undo)
        {
            // отмена добавления убирает вкладку; отказ покажет сам RemoveWorkspace
            if (undo == added)
            {
                if (!AppState.Current.RemoveWorkspace(place.Tab))
                {
                    return false;
                }

                // вкладки больше нет, а значок предлагал бы вернуть ей название
                if (TabActions.TryGetValue(place.Section, out var actions))
                {
                    actions.RemoveAll(s => s is RenameStep rename && ReferenceEquals(rename.Tab, place.Tab));
                }

                return true;
            }

            AppState.Current.ReopenTab(place);
            return true;
        }
    }

    /// <summary>Вкладку переименовали: значок в ряду разделов возвращает прежнее название.</summary>
    private sealed class RenameStep(Workspace tab, string before) : Step
    {
        public Workspace Tab { get; } = tab;

        public override bool Ready() => true;

        public override bool Apply(bool undo)
        {
            Tab.Title = before;
            return true;
        }
    }

    private sealed class Stacks
    {
        public List<Step> Back { get; } = [];

        public List<Step> Forward { get; } = [];
    }

    /// <summary>Память по вкладкам и наборам серверов.</summary>
    private static readonly Dictionary<object, Stacks> Memory = new();

    /// <summary>Закрытые и переименованные вкладки и наборы, у каждого раздела свои. Возврата у них нет.</summary>
    private static readonly Dictionary<AppSection, List<Step>> TabActions = new();

    private static readonly Dictionary<Workspace, TabContent> KnownTabs = new();
    private static ServersContent? _knownServers;
    private static bool _started;
    private static bool _applying;
    private static bool _scheduled;

    /// <summary>Что-то из памяти поменялось: окну пора обновить стрелки.</summary>
    public static event Action? Changed;

    public static bool Covers(AppSection section) =>
        section is AppSection.Files or AppSection.Command or AppSection.Download or AppSection.Servers;

    /// <summary>Чья память сейчас на стрелках: открытая вкладка, на «Серверах» — открытый набор.</summary>
    private static object? CurrentOwner()
    {
        var state = AppState.Current;

        return state.Section switch
        {
            AppSection.Servers => state.ActiveServerSet,
            AppSection.Files or AppSection.Command or AppSection.Download => state.ActiveWorkspace,
            _ => null,
        };
    }

    private static Stacks StacksOf(object owner)
    {
        if (!Memory.TryGetValue(owner, out var stacks))
        {
            Memory[owner] = stacks = new Stacks();
        }

        return stacks;
    }

    public static string? UndoName() => CurrentOwner() is { } owner && StacksOf(owner).Back is { Count: > 0 } steps ? steps[^1].What : null;

    public static string? RedoName() => CurrentOwner() is { } owner && StacksOf(owner).Forward is { Count: > 0 } steps ? steps[^1].What : null;

    /// <summary>Что вернёт значок в ряду разделов; null — значка нет.</summary>
    public static string? TabActionName() =>
        TabActions.TryGetValue(AppState.Current.Section, out var steps) && steps.Count > 0 ? steps[^1].What : null;

    /// <summary>Программа загружена и пароли открыты — с этого состояния и начинается память.</summary>
    public static void Start()
    {
        Rebaseline();
        _started = true;
    }

    /// <summary>Что-то сохранилось. Сверка — одна на всё действие, когда оно закончится: промежуточных шагов нет.</summary>
    public static void Touch()
    {
        if (!_started || _scheduled || Application.Current?.Dispatcher is not { } dispatcher)
        {
            return;
        }

        _scheduled = true;
        dispatcher.BeginInvoke(Observe, DispatcherPriority.Background);
    }

    /// <summary>Пароли стёрты сбросом мастер-пароля: вернуть их отменой нельзя.</summary>
    public static void ForgetServers()
    {
        foreach (var set in Memory.Keys.OfType<ServerSet>().ToList())
        {
            Memory.Remove(set);
        }

        TabActions.Remove(AppSection.Servers);
        _knownServers = AppState.Current.CaptureServers();
        Changed?.Invoke();
    }

    /// <summary>Новая вкладка: отмена — с неё самой, первым шагом её памяти.</summary>
    public static void TabAdded(TabPlace place)
    {
        if (_started && !_applying && Covers(place.Section))
        {
            Push(place.Tab, new TabStep(place, added: true) { What = $"новая вкладка «{place.Tab.Title}»" });
        }
    }

    /// <summary>Закрытая вкладка: отмена — значком в ряду её раздела.</summary>
    public static void TabClosed(TabPlace place)
    {
        if (_started && !_applying && Covers(place.Section))
        {
            PushTabAction(place.Section, new TabStep(place, added: false) { What = $"закрытие вкладки «{place.Tab.Title}»" });
        }
    }

    public static void Undo() => Move(undo: true);

    public static void Redo() => Move(undo: false);

    /// <summary>Значок в ряду разделов: последнее закрытие или переименование в открытом разделе.</summary>
    public static void UndoTabAction()
    {
        if (!TabActions.TryGetValue(AppState.Current.Section, out var steps) || steps.Count == 0 || !steps[^1].Ready())
        {
            return;
        }

        var step = steps[^1];

        _applying = true;

        try
        {
            if (!step.Apply(undo: true))
            {
                return;
            }
        }
        finally
        {
            _applying = false;
        }

        steps.Remove(step);

        Rebaseline();
        Changed?.Invoke();
        Report("Отменено: " + step.What);
    }

    private static void Move(bool undo)
    {
        if (CurrentOwner() is not { } owner)
        {
            return;
        }

        var stacks = StacksOf(owner);
        var from = undo ? stacks.Back : stacks.Forward;

        if (from.Count == 0 || from[^1] is not { } step || !step.Ready())
        {
            return;
        }

        _applying = true;

        try
        {
            if (!step.Apply(undo))
            {
                return;
            }
        }
        finally
        {
            _applying = false;
        }

        from.RemoveAt(from.Count - 1);

        // вкладку могли убрать или вернуть: обратный шаг — там, где человек оказался
        var landed = StacksOf(CurrentOwner() ?? owner);
        (undo ? landed.Forward : landed.Back).Add(step);

        Rebaseline();
        Changed?.Invoke();
        Report((undo ? "Отменено: " : "Возвращено: ") + step.What);
    }

    private static void Report(string text)
    {
        var section = AppState.Current.Section;

        ActionLog.Info(text, null, section, section == AppSection.Servers ? null : AppState.Current.ActiveWorkspace);
    }

    private static IEnumerable<Workspace> Tabs
    {
        get
        {
            var state = AppState.Current;
            return state.Workspaces.Concat(state.CommandTabs).Concat(state.DownloadTabs);
        }
    }

    private static void Rebaseline()
    {
        foreach (var tab in Tabs)
        {
            KnownTabs[tab] = tab.CaptureContent();
        }

        _knownServers = AppState.Current.CaptureServers();
    }

    private static void Observe()
    {
        _scheduled = false;

        if (_applying)
        {
            return;
        }

        // пока курсор в поле, изменение — это набор текста
        var typing = Keyboard.FocusedElement is TextBoxBase;

        foreach (var tab in Tabs)
        {
            var now = tab.CaptureContent();

            // вкладка новая: шаг про неё уже есть, это её начальное содержимое
            if (!KnownTabs.TryGetValue(tab, out var known))
            {
                KnownTabs[tab] = now;
                continue;
            }

            KnownTabs[tab] = now;

            // личная вкладка называется как её родитель: переименование одно, и шаг один
            if (known.Title != now.Title && !tab.IsPersonal)
            {
                PushTabAction(
                    AppState.Current.SectionOfTab(tab),
                    new RenameStep(tab, known.Title) { What = $"переименование вкладки «{known.Title}»" });
            }

            if (known.Signature != now.Signature)
            {
                PushContent(tab, known, now, typing);
            }
        }

        var servers = AppState.Current.CaptureServers();

        if (_knownServers is { } before && before.Signature != servers.Signature)
        {
            if (ServersContent.TabAction(before, servers) is { } action)
            {
                PushTabAction(AppSection.Servers, new ServersStep(before, servers) { What = action });
            }
            else
            {
                Push(AppState.Current.ActiveServerSet, new ServersStep(before, servers) { What = ServersContent.Describe(before, servers) });
            }
        }

        _knownServers = servers;
    }

    private static void PushContent(Workspace tab, TabContent before, TabContent after, bool typing)
    {
        var stacks = StacksOf(tab);

        if (typing
            && stacks.Back.LastOrDefault() is ContentStep { Typing: true } last
            && DateTime.UtcNow - last.At < TypingGap)
        {
            last.After = after;
            last.At = DateTime.UtcNow;
            last.What = TabContent.Describe(last.Before, after);
            stacks.Forward.Clear();
            Changed?.Invoke();
            return;
        }

        Push(tab, new ContentStep(tab, before, after, typing) { What = TabContent.Describe(before, after) });
    }

    private static void PushTabAction(AppSection section, Step step)
    {
        if (!TabActions.TryGetValue(section, out var steps))
        {
            TabActions[section] = steps = [];
        }

        steps.Add(step);

        if (steps.Count > Depth)
        {
            steps.RemoveAt(0);
        }

        Changed?.Invoke();
    }

    private static void Push(object owner, Step step)
    {
        var stacks = StacksOf(owner);

        stacks.Back.Add(step);
        stacks.Forward.Clear();

        if (stacks.Back.Count > Depth)
        {
            stacks.Back.RemoveAt(0);
        }

        Changed?.Invoke();
    }
}
