using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using MultiServerSync.Core;
using MultiServerSync.Models;

namespace MultiServerSync.Services;

/// <summary>Раздел, открытый сейчас — от него зависит, что писать на ярлыках вкладок.</summary>
public enum AppSection
{
    Servers,
    Command,
    Files,
    Settings,
}

/// <summary>
/// Всё пользовательское состояние. Живёт в UserData и переживает полную замену папки Data.
/// Список серверов, избранные команды и история путей общие для всех вкладок;
/// отметки серверов, файлы и команда — у каждой вкладки свои.
/// </summary>
public sealed class AppState : ObservableObject
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    private bool _dirty;
    private bool _loading;

    /// <summary>Открытая вкладка копирования и открытая вкладка команды — у каждого раздела своя.</summary>
    private Workspace? _active;
    private Workspace? _activeCommand;
    private AppSection _section = AppSection.Servers;

    public static AppState Current { get; } = new();

    public ObservableCollection<ServerEntry> Servers { get; } = new();

    /// <summary>Вкладки раздела «Копирование».</summary>
    public ObservableCollection<Workspace> Workspaces { get; } = new();

    /// <summary>
    /// Вкладки раздела «Команда» — свои, с вкладками копирования не связаны: у каждой
    /// своя команда, свои отмеченные серверы и свой терминал.
    /// </summary>
    public ObservableCollection<Workspace> CommandTabs { get; } = new();

    /// <summary>Вкладки открытого раздела: на «Команде» — командные, в остальных — копирования.</summary>
    public ObservableCollection<Workspace> Tabs => _section == AppSection.Command ? CommandTabs : Workspaces;

    /// <summary>Все вкладки обоих разделов — для того, что касается каждой: серверы, наборы, занятость.</summary>
    public IEnumerable<Workspace> AllWorkspaces => Workspaces.Concat(CommandTabs);

    /// <summary>В каком списке живёт вкладка.</summary>
    public ObservableCollection<Workspace> TabsOf(Workspace workspace) =>
        CommandTabs.Contains(workspace) ? CommandTabs : Workspaces;

    /// <summary>Вкладки раздела «Серверы» — с вкладками копирования никак не связаны.</summary>
    public ObservableCollection<ServerSet> ServerSets { get; } = new();

    private ServerSet? _activeSet;

    /// <summary>
    /// Набор, который правится на странице «Серверы» (ярлыки наборов наверху). Какой набор
    /// стоит в панели «Целевые сервера», решает каждая вкладка сама — см. <see cref="PaneSet"/>.
    /// </summary>
    public ServerSet ActiveServerSet
    {
        get => _activeSet ?? ServerSets[0];
        set
        {
            if (value is null || ReferenceEquals(_activeSet, value))
            {
                return;
            }

            _activeSet = value;
            Raise();

            foreach (var set in ServerSets)
            {
                set.RaiseActive();
            }

            ActiveServerSetChanged?.Invoke(value);
            SaveServerSets();
        }
    }

    public event Action<ServerSet>? ActiveServerSetChanged;

    // ── панель «Целевые сервера»: набор, «Все» и одиночный выбор — у открытой вкладки ──

    /// <summary>
    /// Как было до того, как набор стал своим у каждой вкладки: общий выбор «Все» и общий
    /// одиночный выбор на «Всех». Читаются из файла наборов только для переноса во вкладки.
    /// </summary>
    private bool _legacyAllSets;
    private bool _legacyAllSingleSelect = true;
    private bool _legacyAllSingleSelectCopy;

    /// <summary>Вкладка, чья панель сейчас на экране. Пока вкладки не загружены — нет.</summary>
    private Workspace? PaneOwner =>
        (_section == AppSection.Command ? CommandTabs.Count : Workspaces.Count) > 0 ? ActiveWorkspace : null;

    /// <summary>Набор серверов открытой вкладки — он стоит в панели «Целевые сервера».</summary>
    public ServerSet? PaneSet => PaneOwner?.PaneSet;

    /// <summary>
    /// «Все» у открытой вкладки: в панели серверы всех наборов разом, и копирование с командой
    /// идут на отмеченные из любого набора. Щелчок по набору выключает.
    /// </summary>
    public bool PaneAllSets
    {
        get => PaneOwner?.PaneAllSets ?? false;
        set
        {
            if (PaneOwner is not { } owner || owner.PaneAllSets == value)
            {
                return;
            }

            if (value)
            {
                owner.PickAllSets();
            }
            else if (owner.PaneSet is { } set)
            {
                owner.PickPaneSet(set);
            }

            RaisePane();
            EnforceSingleSelect();
        }
    }

    /// <summary>Щелчок по набору внизу левой панели: открытая вкладка переходит на него.</summary>
    public void PickPaneSet(ServerSet set)
    {
        if (PaneOwner is not { } owner || (!owner.PaneAllSets && ReferenceEquals(owner.PaneSet, set)))
        {
            return;
        }

        owner.PickPaneSet(set);
        RaisePane();
        EnforceSingleSelect();
        AppLog.Info($"Целевые сервера — из набора «{set.Title}».", owner.Title);
    }

    /// <summary>Подпись под «Целевые сервера»: какой набор у вкладки.</summary>
    public string PaneSetTitle => PaneAllSets ? "Все" : PaneSet?.Title ?? string.Empty;

    /// <summary>Серверы, из которых открытая вкладка выбирает цели: её набора или, на «Всех», все.</summary>
    public IReadOnlyList<ServerEntry> PaneServers =>
        PaneAllSets || PaneSet is not { } set ? Servers.ToList() : set.Servers;

    /// <summary>Сменилась открытая вкладка или её набор — панель слева и переключатель наборов перечитываются.</summary>
    private void RaisePane()
    {
        foreach (var set in ServerSets)
        {
            set.RaiseActive();
        }

        PaneOwner?.RaiseSection();
        RaiseMany(nameof(PaneSet), nameof(PaneAllSets), nameof(PaneSetTitle), nameof(PaneShowIp), nameof(PaneShowUser));
        RaiseSingleSelect();
    }

    /// <summary>Ряд вкладок — на «Копировании» и на «Команде», у каждой свои вкладки.</summary>
    public bool ShowsWorkspaceTabs => _section is AppSection.Files or AppSection.Command;

    /// <summary>Ряд наборов серверов виден только на «Серверах».</summary>
    public bool ShowsServerSetTabs => _section == AppSection.Servers;

    /// <summary>Переключатель «Одиночный выбор» под серверами — на «Команде» и на «Копировании».</summary>
    public bool ShowsSingleSelectSwitch => _section is AppSection.Command or AppSection.Files;

    /// <summary>Переключатель «Одиночный выбор»: свой у каждой вкладки.</summary>
    public bool SingleSelectHere
    {
        get => PaneOwner?.IsPaneSingleSelect ?? false;
        set
        {
            if (PaneOwner is not { } owner || !ShowsSingleSelectSwitch || owner.SingleSelect == value)
            {
                return;
            }

            // вкладка сама оставляет один сервер или возвращает прежние отметки
            owner.SetSingleSelect(value);
            RaiseSingleSelect();

            AppLog.Info(value
                ? "Одиночный выбор включён."
                : "Одиночный выбор выключен — можно отмечать несколько серверов.", owner.Title);
        }
    }

    /// <summary>Галочки панели работают как кружки: у открытой вкладки включён одиночный выбор.</summary>
    public bool PaneSingleSelect => SingleSelectHere;

    /// <summary>«Отметить все» в одиночном выборе смысла не имеет — кнопку прячем.</summary>
    public bool ShowsToggleAllServers => !PaneSingleSelect;

    private void RaiseSingleSelect() => RaiseMany(
        nameof(ShowsSingleSelectSwitch),
        nameof(SingleSelectHere),
        nameof(PaneSingleSelect),
        nameof(ShowsToggleAllServers));

    /// <summary>В одиночном выборе отмечено несколько — оставляем первый, остальные снимаем.</summary>
    private void EnforceSingleSelect()
    {
        // одиночный выбор свой у каждой вкладки — и действует в её разделе
        foreach (var workspace in Tabs)
        {
            if (workspace.IsPaneSingleSelect)
            {
                workspace.KeepOneServer();
            }
        }
    }

    /// <summary>Строка вкладок наверху: в «Настройках» её нет.</summary>
    public bool ShowsTabStrip => ShowsWorkspaceTabs || ShowsServerSetTabs;

    /// <summary>Наборы в боковой панели под серверами — когда есть из чего выбирать.</summary>
    public bool ShowsSetSwitcher => ServerSets.Count > 1;

    public AppSettings Settings { get; private set; } = new();

    /// <summary>
    /// Галочка «Сохранять лог» из панели вывода. Хранится в настройках, но привязана сразу
    /// к двум окнам — панели внизу и отдельному окну вывода, поэтому стоит здесь:
    /// снятая в одном месте, она сразу снята и в другом.
    /// </summary>
    public bool KeepOutputLog
    {
        get => Settings.KeepOutputLog;
        set
        {
            if (Settings.KeepOutputLog == value)
            {
                return;
            }

            Settings.KeepOutputLog = value;
            Raise();
            SaveSettings();
        }
    }

    /// <summary>
    /// Галочка «IP» под панелью серверов: под каждым сервером — его адрес.
    /// Своя у каждой вкладки «Копирования» и «Команды»; на «Серверах» — у открытой вкладки копирования.
    /// </summary>
    public bool PaneShowIp
    {
        get => PaneOwner?.PaneShowIp ?? false;
        set
        {
            if (PaneOwner is not { } owner || owner.PaneShowIp == value)
            {
                return;
            }

            owner.SetShowIp(value);
            Raise();
            AppLog.Info(value ? "Под целевыми серверами показывается адрес." : "Адрес под целевыми серверами скрыт.", owner.Title);
        }
    }

    /// <summary>Галочка «User» под панелью серверов: под каждым сервером — имя пользователя. Своя у вкладки.</summary>
    public bool PaneShowUser
    {
        get => PaneOwner?.PaneShowUser ?? false;
        set
        {
            if (PaneOwner is not { } owner || owner.PaneShowUser == value)
            {
                return;
            }

            owner.SetShowUser(value);
            Raise();
            AppLog.Info(value ? "Под целевыми серверами показывается имя пользователя." : "Имя пользователя под целевыми серверами скрыто.", owner.Title);
        }
    }

    /// <summary>
    /// Краткие ярлыки вкладок открытого раздела. У «Серверов», «Команды» и «Копирования»
    /// выбор свой: переключение в одном разделе другие не трогает.
    /// </summary>
    public bool CompactTabs
    {
        get => _section switch
        {
            AppSection.Servers => Settings.CompactTabsServers,
            AppSection.Command => Settings.CompactTabsCommand,
            AppSection.Files => Settings.CompactTabsFiles,
            _ => false,
        };
        set
        {
            if (CompactTabs == value)
            {
                return;
            }

            string where;

            switch (_section)
            {
                case AppSection.Servers:
                    Settings.CompactTabsServers = value;
                    where = "«Серверы»";
                    break;
                case AppSection.Command:
                    Settings.CompactTabsCommand = value;
                    where = "«Команда»";
                    break;
                case AppSection.Files:
                    Settings.CompactTabsFiles = value;
                    where = "«Копирование»";
                    break;
                default:
                    return;
            }

            Raise();
            SaveSettings();
            AppLog.Info(value
                ? $"Ярлыки вкладок в разделе {where} — краткие: только название и статус, в два ряда."
                : $"Ярлыки вкладок в разделе {where} — полные: со счётчиками.");
        }
    }

    /// <summary>
    /// Галочки автокопии в терминале показывают выбор того раздела, что открыт: у команд
    /// и у копирования он свой и запоминается отдельно. Везде, кроме «Копирования», — командный.
    /// </summary>
    private bool AutoCopyForCopying => _section == AppSection.Files;

    /// <summary>Подпись галочек: после чего сработает автокопия.</summary>
    public string AutoCopyWhen => AutoCopyForCopying ? "после копирования" : "после команды";

    /// <summary>
    /// Галочка «после …» у «Копировать. Все сервера». Буфер обмена один, поэтому
    /// из двух автокопий работает одна: отметка этой снимает соседнюю.
    /// </summary>
    public bool AutoCopyAll
    {
        get => AutoCopyForCopying ? Settings.AutoCopyAllAfterCopy : Settings.AutoCopyAll;
        set => SetAutoCopy(AutoCopyForCopying, all: value, current: value ? false : AutoCopyCurrent);
    }

    /// <summary>Галочка «после …» у «Копировать. Текущее окно».</summary>
    public bool AutoCopyCurrent
    {
        get => AutoCopyForCopying ? Settings.AutoCopyCurrentAfterCopy : Settings.AutoCopyCurrent;
        set => SetAutoCopy(AutoCopyForCopying, all: value ? false : AutoCopyAll, current: value);
    }

    private void SetAutoCopy(bool forCopying, bool all, bool current)
    {
        if (AutoCopyAll == all && AutoCopyCurrent == current)
        {
            return;
        }

        if (forCopying)
        {
            Settings.AutoCopyAllAfterCopy = all;
            Settings.AutoCopyCurrentAfterCopy = current;
        }
        else
        {
            Settings.AutoCopyAll = all;
            Settings.AutoCopyCurrent = current;
        }

        RaiseMany(nameof(AutoCopyAll), nameof(AutoCopyCurrent));
        SaveSettings();
    }

    /// <summary>Команда или копирование (или повтор упавших) закончились на всех серверах — пора забрать вывод.</summary>
    public event Action<OperationBatch>? OperationFinished;

    public void NotifyFinished(OperationBatch batch) => OperationFinished?.Invoke(batch);

    /// <summary>Это вывод команды, а не копирования или проверки связи.</summary>
    public bool IsCommandBatch(OperationBatch batch) =>
        CommandTabs.Any(w => ReferenceEquals(w.CommandBatch, batch));

    /// <summary>Это вывод копирования — одной вкладки или сводка «Выполнить везде».</summary>
    public bool IsCopyBatch(OperationBatch batch) =>
        Workspaces.Any(w => ReferenceEquals(w.CopyBatch, batch) || ReferenceEquals(w.SummaryBatch, batch));

    /// <summary>Вкладка, чья это операция.</summary>
    public Workspace? OwnerOf(OperationBatch batch) => AllWorkspaces.FirstOrDefault(w =>
        ReferenceEquals(w.CommandBatch, batch)
        || ReferenceEquals(w.CopyBatch, batch)
        || ReferenceEquals(w.SummaryBatch, batch)
        || ReferenceEquals(w.CheckBatch, batch));

    /// <summary>
    /// Вкладка, которую пользователь видит сейчас: на «Команде» — открытая командная,
    /// в остальных разделах — открытая вкладка копирования.
    /// </summary>
    public Workspace ActiveWorkspace
    {
        get => _section == AppSection.Command
            ? _activeCommand ?? CommandTabs[0]
            : _active ?? Workspaces[0];
        set
        {
            if (value is null)
            {
                return;
            }

            var command = CommandTabs.Contains(value);

            if (ReferenceEquals(command ? _activeCommand : _active, value))
            {
                return;
            }

            if (command)
            {
                _activeCommand = value;
            }
            else
            {
                _active = value;
            }

            // вкладка другого раздела стала текущей «за кадром» — на экране ничего не меняется
            if (command != (_section == AppSection.Command))
            {
                SaveWorkspaces();
                return;
            }

            Raise();

            // статус на ярлыке нужен, чтобы не бегать по вкладкам: зашли — он уже не нужен.
            // Метку идущей операции не трогаем: она гаснет сама по её окончании.
            if (value.Status != WorkspaceStatus.Running)
            {
                value.ClearStatus();
            }

            RaiseDim();

            // у вкладки свой набор и свой одиночный выбор — панель слева переключается на них
            RaisePane();
            ActiveWorkspaceChanged?.Invoke(value);
            SaveWorkspaces();
        }
    }

    /// <summary>
    /// Открытый раздел. Ярлыки вкладок пишут статистику по нему: на копировании — файлы,
    /// на команде — размер команды, и всегда число отмеченных серверов.
    /// </summary>
    public AppSection Section
    {
        get => _section;
        set
        {
            if (_section == value)
            {
                return;
            }

            var before = Workspaces.Count > 0 && CommandTabs.Count > 0 ? ActiveWorkspace : null;

            _section = value;

            // лента показывает вкладки открытого раздела — сначала список, потом выбор в нём
            RaiseMany(nameof(Tabs), nameof(ShowsWorkspaceTabs), nameof(ShowsServerSetTabs), nameof(ShowsTabStrip),
                nameof(AutoCopyAll), nameof(AutoCopyCurrent), nameof(AutoCopyWhen), nameof(CompactTabs),
                nameof(PaneShowIp), nameof(PaneShowUser));
            RaisePane();
            EnforceSingleSelect();

            // у копирования и команды свои наборы отмеченных серверов: смена раздела
            // перепривязывает панель слева на нужный набор
            foreach (var workspace in AllWorkspaces)
            {
                workspace.RaiseTabCounts();
                workspace.RaiseSection();
            }

            // у «Команды» и «Копирования» своя открытая вкладка: при переходе между ними
            // текущей становится та, на которой человек был в этом разделе в прошлый раз
            if (before is not null && !ReferenceEquals(before, ActiveWorkspace))
            {
                var now = ActiveWorkspace;

                // зашли на вкладку — итог на её ярлыке увиден
                if (now.Status != WorkspaceStatus.Running)
                {
                    now.ClearStatus();
                }

                Raise(nameof(ActiveWorkspace));
                RaiseDim();
                ActiveWorkspaceChanged?.Invoke(now);
            }
        }
    }

    public event Action<Workspace>? ActiveWorkspaceChanged;

    /// <summary>Просьба показать терминал внизу окна — операция только что запущена.</summary>
    public event Action? TerminalRequested;

    /// <summary>
    /// Просьба показать журнал внизу окна. Нужна, когда операция не начата: причина написана
    /// только в журнале, а нажатие кнопки без него выглядит как «ничего не произошло».
    /// </summary>
    public event Action? LogRequested;

    public void RequestTerminal() => TerminalRequested?.Invoke();

    public void RequestLog() => LogRequested?.Invoke();

    private void RaiseDim()
    {
        foreach (var workspace in AllWorkspaces)
        {
            workspace.RaiseDim();
        }
    }

    /// <summary>Первая вкладка открытого раздела.</summary>
    public Workspace MainWorkspace => Tabs.FirstOrDefault(w => w.IsMain) ?? Tabs[0];

    /// <summary>Отмеченные серверы текущей вкладки.</summary>
    public IReadOnlyList<ServerEntry> SelectedServers => ActiveWorkspace.SelectedServers;

    public void Load()
    {
        Settings = ReadJson<AppSettings>(AppPaths.SettingsFile) ?? new AppSettings();

        var servers = ReadJson<List<ServerEntry>>(AppPaths.ServersFile) ?? new List<ServerEntry>();

        Servers.Clear();
        foreach (var s in servers)
        {
            Track(s);
            Servers.Add(s);
        }

        AppLog.Info($"Загружено серверов: {Servers.Count}");

        LoadServerSets();
        ServerSets.CollectionChanged += (_, _) => Raise(nameof(ShowsSetSwitcher));
        Servers.CollectionChanged += OnServersChanged;

        LoadWorkspaces();

        if (_paneMigrated)
        {
            SaveWorkspaces();
        }
    }

    // ── наборы серверов ────────────────────────────────────────────────────────

    public ServerSet AddServerSet()
    {
        var set = new ServerSet(Guid.NewGuid().ToString("N"), NextSetTitle(), isMain: ServerSets.Count == 0);

        // новый набор пустой: серверы в него добавляются руками
        ServerSets.Add(set);
        SaveServerSets();

        AppLog.Info($"Добавлен набор серверов «{set.Title}». Всего наборов: {ServerSets.Count}.");
        return set;
    }

    /// <summary>
    /// Закрывает набор вместе с его серверами. Подтверждение спрашивает вызывающий:
    /// серверы удаляются насовсем, вернуть их нечем.
    /// </summary>
    public bool RemoveServerSet(ServerSet set)
    {
        if (set.IsMain)
        {
            AppLog.Warn("Первый набор серверов закрыть нельзя.");
            return false;
        }

        if (AllWorkspaces.Any(w => w.IsBusy))
        {
            AppLog.Warn("Идёт операция — дождитесь её окончания, потом закрывайте набор.");
            return false;
        }

        var index = ServerSets.IndexOf(set);

        if (index < 0)
        {
            return false;
        }

        var doomed = set.Servers;

        // сначала уходит вкладка, чтобы удаление серверов не гоняло её пересчёт
        ServerSets.Remove(set);

        if (ReferenceEquals(_activeSet, set))
        {
            ActiveServerSet = ServerSets[Math.Max(0, index - 1)];
        }

        foreach (var server in doomed)
        {
            Servers.Remove(server);
        }

        SaveServers();
        SaveServerSets();

        // вкладки, у которых был этот набор, переходят на первый
        RaisePane();

        AppLog.Warn($"Набор серверов «{set.Title}» закрыт, удалено серверов: {doomed.Count}.");
        return true;
    }

    public void SaveServerSets()
    {
        if (_loading || ServerSets.Count == 0)
        {
            return;
        }

        WriteJson(AppPaths.ServerSetsFile, new ServerSetsFile
        {
            ActiveId = ActiveServerSet.Id,
            Sets = ServerSets.Select(s => s.ToState()).ToList(),
        });
    }

    private void LoadServerSets()
    {
        _loading = true;

        try
        {
            var file = ReadJson<ServerSetsFile>(AppPaths.ServerSetsFile);
            var states = file?.Sets ?? new List<ServerSetState>();

            // до наборов серверы жили одним списком — он и становится первым набором
            if (states.Count == 0)
            {
                states.Add(new ServerSetState { Id = Guid.NewGuid().ToString("N"), Title = "Основной" });
            }

            for (var i = 0; i < states.Count; i++)
            {
                var state = states[i];
                var set = new ServerSet(
                    string.IsNullOrWhiteSpace(state.Id) ? Guid.NewGuid().ToString("N") : state.Id,
                    string.IsNullOrWhiteSpace(state.Title) ? (i == 0 ? "Основной" : $"Набор {i + 1}") : state.Title,
                    isMain: i == 0);

                set.RestoreSingleSelect(state.SingleSelect ?? true, state.SingleSelectCopy);
                ServerSets.Add(set);
            }

            // сервер без набора или из пропавшего набора не теряем — он уходит в первый
            var known = ServerSets.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            var adopted = 0;

            foreach (var server in Servers.Where(s => !known.Contains(s.SetId)))
            {
                server.SetId = ServerSets[0].Id;
                adopted++;
            }

            foreach (var set in ServerSets)
            {
                set.Sync(Servers);
            }

            _activeSet = ServerSets.FirstOrDefault(s => s.Id == file?.ActiveId) ?? ServerSets[0];
            _legacyAllSets = file?.LegacyShowAll == true;
            _legacyAllSingleSelect = file?.LegacyAllSingleSelect ?? true;
            _legacyAllSingleSelectCopy = file?.LegacyAllSingleSelectCopy == true;

            AppLog.Info($"Наборов серверов: {ServerSets.Count}. Текущий: «{ActiveServerSet.Title}».");

            if (adopted > 0 || file is null)
            {
                _loading = false;
                SaveServers();
                SaveServerSets();
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private string NextSetTitle()
    {
        for (var n = ServerSets.Count + 1; ; n++)
        {
            var title = $"Набор {n}";

            if (ServerSets.All(s => !string.Equals(s.Title, title, StringComparison.OrdinalIgnoreCase)))
            {
                return title;
            }
        }
    }

    // ── вкладки ────────────────────────────────────────────────────────────────

    /// <summary>Новая вкладка в открытом разделе — копирования или команды.</summary>
    public Workspace AddWorkspace(string? title = null)
    {
        var tabs = Tabs;
        var source = tabs.Count > 0 ? ActiveWorkspace : null;

        var workspace = new Workspace(
            Guid.NewGuid().ToString("N"),
            string.IsNullOrWhiteSpace(title) ? NextTitle(tabs) : title.Trim(),
            isMain: tabs.Count == 0);

        workspace.SyncServers(Servers);

        // новая вкладка начинается с условий текущей: те же серверы и те же параметры,
        // но со своим (пустым) списком файлов и своей командой
        workspace.Restore(new WorkspaceState
        {
            RemotePath = source?.RemotePath ?? Settings.LastRemotePath,
            OverwriteFiles = source?.OverwriteFiles ?? Settings.OverwriteFiles,
            PreserveTimestamps = source?.PreserveTimestamps ?? Settings.PreserveTimestamps,
            VerifyChecksums = source?.VerifyChecksums ?? Settings.VerifyChecksums,
            MatchByDate = source?.MatchByDate ?? false,
            MatchByHash = source?.MatchByHash ?? false,
        });

        if (source is not null)
        {
            // оба набора галочек переезжают на новую вкладку такими же, как были, —
            // во всех наборах серверов, а не только в открытом: у каждого набора своя память
            workspace.SelectServers(source.CopyServers.Where(c => c.IsSelected).Select(c => c.Server.Id));
            workspace.SelectCommandServers(source.CommandServers.Where(c => c.IsSelected).Select(c => c.Server.Id));

            // и тот же набор серверов с тем же одиночным выбором — дальше у неё своя память
            workspace.CopyPaneFrom(source);
        }
        else
        {
            workspace.InitPane(ActiveServerSet.Id, allSets: false, new Dictionary<string, bool>(), showIp: false, showUser: false);
        }

        tabs.Add(workspace);
        SaveWorkspaces();

        AppLog.Info($"Добавлена вкладка «{workspace.Title}». Всего вкладок: {tabs.Count}.");
        return workspace;
    }

    /// <summary>
    /// Перетаскивание ярлыков мышью: вкладка встаёт на место <paramref name="to"/>.
    /// Первая вкладка закреплена: её саму не двигаем и никого перед ней не пускаем —
    /// иначе после перезапуска «основной» станет другая.
    /// </summary>
    public bool MoveWorkspace(Workspace dragged, int to)
    {
        if (dragged.IsMain)
        {
            return false;
        }

        var tabs = TabsOf(dragged);
        var from = tabs.IndexOf(dragged);

        if (from < 0)
        {
            return false;
        }

        to = Math.Clamp(to, 1, tabs.Count - 1);

        if (from == to)
        {
            return false;
        }

        tabs.Move(from, to);
        AppLog.Debug($"Вкладка «{dragged.Title}» переставлена: место {from + 1} → {to + 1}.");
        SaveWorkspaces();
        return true;
    }

    /// <summary>То же для наборов серверов: первый набор закреплён по той же причине.</summary>
    public bool MoveServerSet(ServerSet dragged, int to)
    {
        if (dragged.IsMain)
        {
            return false;
        }

        var from = ServerSets.IndexOf(dragged);

        if (from < 0)
        {
            return false;
        }

        to = Math.Clamp(to, 1, ServerSets.Count - 1);

        if (from == to)
        {
            return false;
        }

        ServerSets.Move(from, to);
        SaveServerSets();
        return true;
    }

    public bool RemoveWorkspace(Workspace workspace)
    {
        if (workspace.IsMain)
        {
            AppLog.Warn("Первую вкладку закрыть нельзя.");
            return false;
        }

        if (workspace.IsBusy)
        {
            AppLog.Warn($"Вкладка «{workspace.Title}» занята операцией — сначала дождитесь её окончания.");
            return false;
        }

        var tabs = TabsOf(workspace);
        var index = tabs.IndexOf(workspace);

        if (index < 0)
        {
            return false;
        }

        tabs.Remove(workspace);
        workspace.StopWatching();

        if (ReferenceEquals(_active, workspace) || ReferenceEquals(_activeCommand, workspace))
        {
            ActiveWorkspace = tabs[Math.Max(0, index - 1)];
        }

        SaveWorkspaces();
        AppLog.Info($"Вкладка «{workspace.Title}» закрыта.");
        return true;
    }

    public void SaveWorkspaces()
    {
        if (_loading || Workspaces.Count == 0 || CommandTabs.Count == 0)
        {
            return;
        }

        WriteJson(AppPaths.WorkspacesFile, new WorkspacesFile
        {
            ActiveId = (_active ?? Workspaces[0]).Id,
            Workspaces = Workspaces.Select(w => w.ToState()).ToList(),
            ActiveCommandId = (_activeCommand ?? CommandTabs[0]).Id,
            CommandTabs = CommandTabs.Select(w => w.ToState()).ToList(),
        });
    }

    // ── сохранение ─────────────────────────────────────────────────────────────

    public void MarkDirty() => _dirty = true;

    public void SaveServers()
    {
        // в servers.json отметки остаются от копирования первой вкладки — набор для команды
        // живёт только в workspaces.json, иначе два набора спорили бы за одно поле
        var main = Workspaces.FirstOrDefault(w => w.IsMain);

        if (main is not null)
        {
            foreach (var check in main.CopyServers)
            {
                check.Server.IsSelected = check.IsSelected;
            }
        }

        WriteJson(AppPaths.ServersFile, Servers.ToList());
        _dirty = false;
    }

    public void SaveSettings() => WriteJson(AppPaths.SettingsFile, Settings);

    public void SaveAll()
    {
        SaveServers();
        SaveSettings();
        SaveServerSets();
        SaveWorkspaces();
    }

    public void SaveIfDirty()
    {
        if (_dirty)
        {
            SaveServers();
        }
    }

    private void LoadWorkspaces()
    {
        _loading = true;

        try
        {
            var file = ReadJson<WorkspacesFile>(AppPaths.WorkspacesFile);
            var states = file?.Workspaces ?? new List<WorkspaceState>();

            if (states.Count == 0)
            {
                states.Add(MigrateFromSettings());
            }

            for (var i = 0; i < states.Count; i++)
            {
                var state = states[i];
                var workspace = new Workspace(
                    string.IsNullOrWhiteSpace(state.Id) ? Guid.NewGuid().ToString("N") : state.Id,
                    string.IsNullOrWhiteSpace(state.Title) ? (i == 0 ? "Основная" : $"Вкладка {i + 1}") : state.Title,
                    isMain: i == 0);

                workspace.SyncServers(Servers);
                workspace.SelectServers(state.SelectedServerIds);

                // файл старого формата: набора для команды в нём нет — берём тот же, что у копирования,
                // чтобы после обновления вкладки работали ровно как раньше
                workspace.SelectCommandServers(state.CommandServerIds ?? state.SelectedServerIds);
                workspace.Restore(state);
                InitLegacyPane(workspace, state, copying: true);

                Workspaces.Add(workspace);
            }

            _active = Workspaces.FirstOrDefault(w => w.Id == file?.ActiveId) ?? Workspaces[0];

            // вкладки команды. Файл без них — из версии, где на «Команде» работала основная
            // вкладка копирования: её команда и отметки переезжают в первую командную
            var commandStates = file?.CommandTabs ?? new List<WorkspaceState>();
            var migrated = commandStates.Count == 0;

            if (migrated)
            {
                var main = Workspaces[0];

                commandStates.Add(new WorkspaceState
                {
                    Title = "Основная",
                    IsMain = true,
                    CommandServerIds = main.CommandServers.Where(c => c.IsSelected).Select(c => c.Server.Id).ToList(),
                    CommandText = main.CommandText,
                    UseSudo = main.UseSudo,
                });
            }

            for (var i = 0; i < commandStates.Count; i++)
            {
                var state = commandStates[i];
                var tab = new Workspace(
                    string.IsNullOrWhiteSpace(state.Id) ? Guid.NewGuid().ToString("N") : state.Id,
                    string.IsNullOrWhiteSpace(state.Title) ? (i == 0 ? "Основная" : $"Вкладка {i + 1}") : state.Title,
                    isMain: i == 0);

                tab.SyncServers(Servers);
                tab.SelectCommandServers(state.CommandServerIds ?? new List<string>());
                tab.Restore(state);
                InitLegacyPane(tab, state, copying: false);

                CommandTabs.Add(tab);
            }

            _activeCommand = CommandTabs.FirstOrDefault(w => w.Id == file?.ActiveCommandId) ?? CommandTabs[0];

            AppLog.Info($"Вкладок копирования: {Workspaces.Count}, команды: {CommandTabs.Count}.");
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// Вкладкам перенесли общий выбор из прежней версии. Сохраняем их сразу: файл наборов
    /// общий выбор больше не пишет, и при следующем его сохранении перенести было бы уже нечего.
    /// </summary>
    private bool _paneMigrated;

    /// <summary>
    /// Вкладка из файла прежней версии, где набор, «Все» и одиночный выбор были общими:
    /// она получает то, что было выбрано, — после обновления всё выглядит как раньше.
    /// </summary>
    private void InitLegacyPane(Workspace workspace, WorkspaceState state, bool copying)
    {
        // галочки «IP» и «User» до 2.0.9 были у раздела — вкладка получает галочки своего раздела
        var showIp = copying ? Settings.PaneShowIpFiles : Settings.PaneShowIpCommand;
        var showUser = copying ? Settings.PaneShowUserFiles : Settings.PaneShowUserCommand;

        if (state.PaneSetId is not null)
        {
            if (state.PaneShowIp is null)
            {
                _paneMigrated = true;
                workspace.InitView(showIp, showUser);
            }

            return;
        }

        _paneMigrated = true;

        // одиночный выбор был у каждого набора свой (общий для всех вкладок) — у каждого набора он и остаётся
        var singleFor = ServerSets.ToDictionary(
            s => s.Id,
            s => copying ? s.SingleSelectCopy : s.SingleSelect,
            StringComparer.Ordinal);
        singleFor["*"] = copying ? _legacyAllSingleSelectCopy : _legacyAllSingleSelect;

        workspace.InitPane(ActiveServerSet.Id, _legacyAllSets, singleFor, showIp, showUser);
    }

    /// <summary>Первый запуск после обновления: старые общие настройки становятся первой вкладкой.</summary>
    private WorkspaceState MigrateFromSettings() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Title = "Основная",
        IsMain = true,
        SelectedServerIds = Servers.Where(s => s.IsSelected).Select(s => s.Id).ToList(),
        CopyList = Settings.CopyList.ToList(),
        RemotePath = Settings.LastRemotePath,
        OverwriteFiles = Settings.OverwriteFiles,
        PreserveTimestamps = Settings.PreserveTimestamps,
        VerifyChecksums = Settings.VerifyChecksums,
    };

    private static string NextTitle(IList<Workspace> tabs)
    {
        for (var n = tabs.Count + 1; ; n++)
        {
            var title = $"Вкладка {n}";

            if (tabs.All(w => !string.Equals(w.Title, title, StringComparison.OrdinalIgnoreCase)))
            {
                return title;
            }
        }
    }

    private void OnServersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var item in e.NewItems?.OfType<ServerEntry>() ?? [])
        {
            Track(item);
        }

        foreach (var item in e.OldItems?.OfType<ServerEntry>() ?? [])
        {
            item.PropertyChanged -= OnServerPropertyChanged;
        }

        foreach (var set in ServerSets)
        {
            set.Sync(Servers);
        }

        // новый сервер не отмечается нигде — ни на одной вкладке, ни в одном разделе
        foreach (var workspace in AllWorkspaces)
        {
            workspace.SyncServers(Servers);
        }

        SaveWorkspaces();
        MarkDirty();
    }

    private void Track(ServerEntry entry)
    {
        entry.PropertyChanged -= OnServerPropertyChanged;
        entry.PropertyChanged += OnServerPropertyChanged;
    }

    private void OnServerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ServerEntry.LastStatus) or nameof(ServerEntry.Display))
        {
            return;
        }

        MarkDirty();
    }

    private static T? ReadJson<T>(string path)
        where T : class
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Не удалось прочитать {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    private static void WriteJson<T>(string path, T value)
    {
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Не удалось сохранить {Path.GetFileName(path)}: {ex.Message}");
        }
    }
}
