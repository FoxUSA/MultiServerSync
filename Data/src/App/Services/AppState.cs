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
    Download,
    Explorer,
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
    private Workspace? _activeDownload;
    private Workspace? _activeExplorer;

    /// <summary>
    /// Вкладка «Команд», открытая до перехода стрелкой в личные команды, и сама личная вкладка:
    /// уйдём из раздела — он снова откроется на выбранной человеком вкладке, а не на личной.
    /// </summary>
    private Workspace? _commandBeforeJump;
    private Workspace? _jumpedTo;
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
    /// <summary>Вкладки раздела «Скачивание» — свои, отдельные от копирования и команды.</summary>
    public ObservableCollection<Workspace> DownloadTabs { get; } = new();

    /// <summary>Вкладки раздела «Проводник»: у каждой свой сервер, свои папки и параметры переноса.</summary>
    public ObservableCollection<Workspace> ExplorerTabs { get; } = new();

    public ObservableCollection<Workspace> Tabs => TabsFor(_section);

    private ObservableCollection<Workspace> TabsFor(AppSection section) => section switch
    {
        AppSection.Command => CommandTabs,
        AppSection.Download => DownloadTabs,
        AppSection.Explorer => ExplorerTabs,
        _ => Workspaces,
    };

    /// <summary>Раздел, чьи вкладки сейчас в ленте: «Серверы» и «Настройки» показывают копирование.</summary>
    private AppSection TabSection =>
        _section is AppSection.Command or AppSection.Download or AppSection.Explorer ? _section : AppSection.Files;

    private AppSection SectionOf(Workspace workspace) =>
        CommandTabs.Contains(workspace) ? AppSection.Command
        : DownloadTabs.Contains(workspace) ? AppSection.Download
        : ExplorerTabs.Contains(workspace) ? AppSection.Explorer
        : AppSection.Files;

    /// <summary>Все вкладки обоих разделов — для того, что касается каждой: серверы, наборы, занятость.</summary>
    public IEnumerable<Workspace> AllWorkspaces => Workspaces.Concat(CommandTabs).Concat(DownloadTabs).Concat(ExplorerTabs);

    /// <summary>В каком списке живёт вкладка.</summary>
    public ObservableCollection<Workspace> TabsOf(Workspace workspace) => TabsFor(SectionOf(workspace));

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
        Tabs.Count > 0 ? ActiveWorkspace : null;

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
        AppLog.Hint($"Целевые сервера — из набора «{set.Title}».");
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
    public bool ShowsWorkspaceTabs =>
        _section is AppSection.Files or AppSection.Command or AppSection.Download or AppSection.Explorer;

    /// <summary>Ряд наборов серверов виден только на «Серверах».</summary>
    public bool ShowsServerSetTabs => _section == AppSection.Servers;

    /// <summary>Переключатель «Одиночный выбор» под серверами — на «Команде» и на «Копировании».</summary>
    public bool ShowsSingleSelectSwitch => _section is AppSection.Command or AppSection.Files or AppSection.Download;

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

            AppLog.Hint(value
                ? "Одиночный выбор включён."
                : "Одиночный выбор выключен — можно отмечать несколько серверов.");
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

    private const int MaxOutputLife = 99 * 60;

    /// <summary>Срок жизни вывода в терминале: общий для всех вкладок, кнопками у терминала.</summary>
    public TimeSpan OutputLife => TimeSpan.FromSeconds(Math.Clamp(Settings.OutputLifeSeconds, 0, MaxOutputLife));

    /// <summary>
    /// «−» и «+» у срока: по 30 секунд, а у нуля мельче — ниже 30 секунд по 10, ниже 10 по 5,
    /// ниже 5 по одной. Вверх — те же ступени обратно; потолок — 99 минут.
    /// </summary>
    public void StepOutputLife(int direction)
    {
        var s = (int)OutputLife.TotalSeconds;

        s = direction > 0
            ? s + (s < 5 ? 1 : s < 10 ? 5 : s < 30 ? 10 : 30)
            : s - (s <= 5 ? 1 : s <= 10 ? 5 : s <= 30 ? 10 : 30);

        s = Math.Clamp(s, 0, MaxOutputLife);

        if (Settings.OutputLifeSeconds == s)
        {
            return;
        }

        Settings.OutputLifeSeconds = s;
        SaveSettings();
        TickOutputLife();
    }

    /// <summary>Раз в секунду: окошки срока отсчитывают, у кого вышло — терминал вкладки очищается.</summary>
    public void TickOutputLife()
    {
        foreach (var workspace in AllWorkspaces)
        {
            workspace.TickOutputLife();
        }
    }

    /// <summary>Шрифт вывода — общий у панели под выводом и отдельного окна.</summary>
    public double OutputFontSize => Math.Clamp(Settings.OutputFontSize, MinOutputFont, MaxOutputFont);

    private const double MinOutputFont = 9;
    private const double MaxOutputFont = 28;

    /// <summary>Лупа: шрифт вывода на <paramref name="step"/> точек крупнее (минус — мельче).</summary>
    public void ZoomOutput(double step)
    {
        var size = Math.Clamp(OutputFontSize + step, MinOutputFont, MaxOutputFont);

        if (size == Settings.OutputFontSize)
        {
            return;
        }

        Settings.OutputFontSize = size;
        Raise(nameof(OutputFontSize));
        SaveSettings();
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
            AppLog.Hint(value ? "Под целевыми серверами показывается адрес." : "Адрес под целевыми серверами скрыт.");
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
            AppLog.Hint(value ? "Под целевыми серверами показывается имя пользователя." : "Имя пользователя под целевыми серверами скрыто.");
        }
    }

    /// <summary>
    /// Галочки автокопии в терминале показывают выбор того раздела, что открыт: у команд
    /// и у копирования он свой и запоминается отдельно. Везде, кроме «Копирования», — командный.
    /// </summary>
    private bool AutoCopyForCopying => _section is AppSection.Files or AppSection.Download or AppSection.Explorer;

    /// <summary>
    /// Галочка «авто» у «Все логи». Буфер обмена один, поэтому
    /// из двух автокопий работает одна: отметка этой снимает соседнюю.
    /// </summary>
    public bool AutoCopyAll
    {
        get => AutoCopyForCopying ? Settings.AutoCopyAllAfterCopy : Settings.AutoCopyAll;
        set => SetAutoCopy(AutoCopyForCopying, all: value, current: value ? false : AutoCopyCurrent);
    }

    /// <summary>Галочка «авто» у «Текущий лог».</summary>
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

    /// <summary>Галочка «+ команды» у копирования вывода: копировать и вывод личных команд вкладки.</summary>
    public bool CopyWithChain
    {
        get => Settings.CopyWithChain;
        set
        {
            if (Settings.CopyWithChain == value)
            {
                return;
            }

            Settings.CopyWithChain = value;
            Raise();
            SaveSettings();
            AppLog.Hint(value
                ? "Копирование вывода в разделе «Копирование» берёт и вывод личных команд вкладки."
                : "Копирование вывода — только копирование, без личных команд.");
        }
    }

    /// <summary>Галочка «+ команды» есть только в разделе «Копирование».</summary>
    public System.Windows.Visibility ChainCopyVisibility =>
        _section == AppSection.Files ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    /// <summary>
    /// Личные команды строки копирования — для поля вывода, которое показывает их следом:
    /// галочка «+ команды» стоит и у вкладки включены команды после копирования. Иначе null.
    /// </summary>
    /// <summary>
    /// Строка команд после копирования, что шла следом за этой строкой копирования. Прогоны
    /// копятся, у сервера строк бывает несколько: подходит строка команд того же сервера,
    /// появившаяся после этой строки и раньше следующей строки копирования этого сервера.
    /// </summary>
    public static OperationResult? AfterRowOf(OperationBatch copy, OperationResult row, OperationBatch chain)
    {
        var next = copy.Results
            .Where(r => ReferenceEquals(r.Server, row.Server) && r.Created > row.Created)
            .Select(r => (DateTime?)r.Created)
            .Min();

        return chain.Results.LastOrDefault(c => ReferenceEquals(c.Server, row.Server)
            && c.Created >= row.Created
            && (next is null || c.Created < next));
    }

    /// <summary>То же по одной строке копирования: её вкладка и команды после копирования находятся сами.</summary>
    public OperationResult? AfterRowOf(OperationResult copyRow) =>
        ChainBatchOf(copyRow) is { } chain
        && Workspaces.FirstOrDefault(w => w.CopyBatch.Results.Contains(copyRow)) is { } owner
            ? AfterRowOf(owner.CopyBatch, copyRow, chain)
            : null;

    public OperationBatch? ChainBatchOf(OperationResult copyRow)
    {
        if (!Settings.CopyWithChain)
        {
            return null;
        }

        var owner = Workspaces.FirstOrDefault(w => w.CopyBatch.Results.Contains(copyRow));
        return owner is { AfterCopyEnabled: true, Personal: { } personal } ? personal.CommandBatch : null;
    }

    /// <summary>
    /// Команды после копирования этой вкладки — их строки встают в таблице терминала под строками
    /// копирования. Null — у вкладки их нет или они выключены.
    /// </summary>
    public OperationBatch? AfterCopyBatchOf(OperationBatch copy)
    {
        var owner = Workspaces.FirstOrDefault(w => ReferenceEquals(w.CopyBatch, copy));
        return owner is { AfterCopyEnabled: true, Personal: { } personal } ? personal.CommandBatch : null;
    }

    /// <summary>
    /// Вывод личных команд к выводу копирования — если галочка «+ команды» стоит, у вкладки
    /// включены команды после копирования и они уже что-то вывели. Иначе null.
    /// </summary>
    public OperationBatch? ChainFor(OperationBatch copy)
    {
        if (!Settings.CopyWithChain)
        {
            return null;
        }

        var owner = Workspaces.FirstOrDefault(w => ReferenceEquals(w.CopyBatch, copy));
        return owner is { AfterCopyEnabled: true, Personal: { } personal } && personal.CommandBatch.HasResults
            ? personal.CommandBatch
            : null;
    }

    /// <summary>Команда или копирование (или повтор упавших) закончились на всех серверах — пора забрать вывод.</summary>
    public event Action<OperationBatch>? OperationFinished;

    public void NotifyFinished(OperationBatch batch) => OperationFinished?.Invoke(batch);

    /// <summary>Это вывод команды — одной вкладки или сводка «Выполнить везде», а не копирования или проверки связи.</summary>
    public bool IsCommandBatch(OperationBatch batch) =>
        CommandTabs.Any(w => ReferenceEquals(w.CommandBatch, batch) || ReferenceEquals(w.SummaryBatch, batch));

    /// <summary>Это вывод копирования — одной вкладки или сводка «Выполнить везде».</summary>
    public bool IsCopyBatch(OperationBatch batch) =>
        Workspaces.Concat(DownloadTabs).Concat(ExplorerTabs)
            .Any(w => ReferenceEquals(w.CopyBatch, batch) || ReferenceEquals(w.SummaryBatch, batch));

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
        get => TabSection switch
        {
            AppSection.Command => _activeCommand ?? CommandTabs[0],
            AppSection.Download => _activeDownload ?? DownloadTabs[0],
            AppSection.Explorer => _activeExplorer ?? ExplorerTabs[0],
            _ => _active ?? Workspaces[0],
        };
        set
        {
            if (value is null)
            {
                return;
            }

            var section = SectionOf(value);
            var current = section switch
            {
                AppSection.Command => _activeCommand,
                AppSection.Download => _activeDownload,
                AppSection.Explorer => _activeExplorer,
                _ => _active,
            };

            if (ReferenceEquals(current, value))
            {
                return;
            }

            switch (section)
            {
                case AppSection.Command:
                    _activeCommand = value;

                    // выбрал в «Командах» другую вкладку сам — она и запоминается
                    if (!ReferenceEquals(value, _jumpedTo))
                    {
                        _commandBeforeJump = null;
                        _jumpedTo = null;
                    }

                    break;
                case AppSection.Download:
                    _activeDownload = value;
                    break;
                case AppSection.Explorer:
                    _activeExplorer = value;
                    break;
                default:
                    _active = value;
                    break;
            }

            // вкладка другого раздела стала текущей «за кадром» — на экране ничего не меняется
            if (section != TabSection)
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

            var before = TabsLoaded ? ActiveWorkspace : null;

            var leaving = _section;
            _section = value;

            if (leaving == AppSection.Command && value != AppSection.Command)
            {
                ReturnFromJump();
            }

            // лента показывает вкладки открытого раздела — сначала список, потом выбор в нём
            RaiseMany(nameof(Section), nameof(Tabs), nameof(ShowsWorkspaceTabs), nameof(ShowsServerSetTabs), nameof(ShowsTabStrip),
                nameof(AutoCopyAll), nameof(AutoCopyCurrent), nameof(ChainCopyVisibility),
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

        AppLog.Debug($"Загружено серверов: {Servers.Count}");

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

        AppLog.Hint($"Добавлен набор серверов «{set.Title}». Всего наборов: {ServerSets.Count}.");
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
            AppLog.Hint("Первый набор серверов закрыть нельзя.", LogLevel.Warning);
            return false;
        }

        if (AllWorkspaces.Any(w => w.IsBusy))
        {
            AppLog.Hint("Идёт операция — дождитесь её окончания, потом закрывайте набор.", LogLevel.Warning);
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

            AppLog.Debug($"Наборов серверов: {ServerSets.Count}. Текущий: «{ActiveServerSet.Title}».");

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
            ReplaceWithoutAsking = source?.ReplaceWithoutAsking ?? false,
            ExcludeMasks = source?.ExcludeMasks ?? string.Empty,
            MatchByDate = source?.MatchByDate ?? false,
            MatchByHash = source?.MatchByHash ?? false,
        });

        // новая вкладка проводника открывается там же, где стоит текущая, — как новая вкладка в Windows
        workspace.Explorer = source?.Explorer.Clone() ?? new ExplorerTabState();

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

        AppLog.Hint($"Добавлена вкладка «{workspace.Title}». Всего вкладок: {tabs.Count}.");
        return workspace;
    }

    /// <summary>Открытая вкладка «Копирования» — даже когда на экране другой раздел.</summary>
    public Workspace ActiveCopyTab => _active ?? Workspaces[0];

    /// <summary>Открытая вкладка «Скачивания» — даже когда на экране другой раздел.</summary>
    public Workspace ActiveDownloadTab => _activeDownload ?? DownloadTabs[0];

    /// <summary>Просьба показать вкладку другого раздела: окно переключает раздел и вкладку.</summary>
    public event Action<Workspace>? TabShowRequested;

    public void ShowTab(Workspace tab) => TabShowRequested?.Invoke(tab);

    /// <summary>
    /// Переход стрелкой с ярлыка копирования в его личные команды — быстрая ссылка: выбор
    /// вкладки в «Командах» она не меняет, вернутся в раздел обычным путём — там прежняя вкладка.
    /// </summary>
    public void JumpToPersonal(Workspace personal)
    {
        if (_commandBeforeJump is null && !ReferenceEquals(_activeCommand ?? CommandTabs[0], personal))
        {
            _commandBeforeJump = _activeCommand ?? CommandTabs[0];
        }

        _jumpedTo = personal;
    }

    private void ReturnFromJump()
    {
        if (_commandBeforeJump is { } before && ReferenceEquals(_activeCommand, _jumpedTo) && CommandTabs.Contains(before))
        {
            _activeCommand = before;
            SaveWorkspaces();
        }

        _commandBeforeJump = null;
        _jumpedTo = null;
    }

    /// <summary>
    /// Личные вкладки — у правого края «Команд», в конце ленты, в порядке их вкладок копирования;
    /// перетаскиванием их не разбросать — после переноса строй восстанавливается.
    /// </summary>
    private void ArrangePersonalTabs()
    {
        var personal = CommandTabs
            .Where(t => t.IsPersonal)
            .OrderBy(t => t.Owner is { } owner ? Workspaces.IndexOf(owner) : int.MaxValue)
            .ToList();

        // уже стоят как надо — не трогаем: лишняя перестановка дёргает выбор на ленте
        if (CommandTabs.Skip(CommandTabs.Count - personal.Count).SequenceEqual(personal))
        {
            return;
        }

        // по одной в конец ленты — в итоге хвост ровно в нужном порядке
        foreach (var tab in personal)
        {
            CommandTabs.Move(CommandTabs.IndexOf(tab), CommandTabs.Count - 1);
        }
    }

    /// <summary>Раздел, к которому относится вкладка.</summary>
    public AppSection SectionOfTab(Workspace tab) => SectionOf(tab);

    /// <summary>
    /// Кнопка «Проводник» в «Серверах»: всегда основная вкладка проводника, новых не заводим.
    /// Справа — домашняя папка этого сервера, слева — «Этот компьютер» со всеми дисками.
    /// </summary>
    public Workspace ExplorerFor(ServerEntry server)
    {
        var tab = ExplorerTabs.FirstOrDefault(t => t.IsMain) ?? ExplorerTabs[0];

        var state = tab.Explorer;
        state.Local.Path = string.Empty;
        state.Local.Focus = string.Empty;
        state.RemoteServerId = server.Id;
        state.Remote.Path = string.Empty;
        state.Remote.Focus = string.Empty;
        state.RemoteActive = true;

        // сервера нет в наборе вкладки — она смотрит на «Все», иначе он не встанет отмеченным
        if (!tab.PaneAllSets && tab.PaneSet is { } set && !set.Servers.Contains(server))
        {
            tab.PickAllSets();
        }

        tab.SelectServers([server.Id]);
        SaveWorkspaces();
        return tab;
    }

    /// <summary>
    /// Команда из проводника («команда здесь», «запустить») — как «Проводник» в «Серверах»:
    /// всегда основная вкладка «Команды», новых не заводим. Слева отмечен только этот сервер,
    /// в первом окне — готовая команда. Сама команда не запускается.
    /// </summary>
    public Workspace CommandFor(ServerEntry server, string command)
    {
        var tab = CommandTabs.FirstOrDefault(t => t.IsMain) ?? CommandTabs[0];

        // сервера нет в наборе вкладки — она смотрит на «Все», иначе он не встанет отмеченным
        if (!tab.PaneAllSets && tab.PaneSet is { } set && !set.Servers.Contains(server))
        {
            tab.PickAllSets();
        }

        tab.SelectCommandServers([server.Id]);
        tab.CommandText = command;
        SaveWorkspaces();
        AppLog.Hint($"Команда для {server.PaneLabel} — в основной вкладке «Команды».");
        return tab;
    }

    /// <summary>
    /// «Добавить в скачивание» из проводника: в основную вкладку «Скачивания», строками ниже
    /// уже имеющихся — список не затирается.
    /// </summary>
    public Workspace MainDownloadTab => DownloadTabs.FirstOrDefault(t => t.IsMain) ?? DownloadTabs[0];

    /// <summary>
    /// Личная командная вкладка для вкладки копирования: пустая, с тем же названием,
    /// серверы берёт у родителя. Встаёт в конец ленты «Команды».
    /// </summary>
    public Workspace CreatePersonalTab(Workspace copy)
    {
        var personal = new Workspace(Guid.NewGuid().ToString("N"), copy.Title, isMain: false)
        {
            OwnerCopyTabId = copy.Id,
        };

        // при создании — те же наборы и отметки, что у копирования; дальше у личной своя память
        personal.SyncServers(Servers);
        personal.SelectCommandServers(copy.CopyServers.Where(c => c.IsSelected).Select(c => c.Server.Id));
        personal.CopyPaneFrom(copy);

        CommandTabs.Add(personal);
        copy.LinkPersonal(personal);
        ArrangePersonalTabs();
        SaveWorkspaces();

        AppLog.Hint($"Создана личная вкладка команд для «{copy.Title}»: выполнится после удачного копирования.");
        return personal;
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

        // личные вкладки держат строй у правого края «Команд» — за порядком копирования
        if (ReferenceEquals(tabs, CommandTabs) || ReferenceEquals(tabs, Workspaces))
        {
            ArrangePersonalTabs();
        }

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

    /// <summary>
    /// Перетаскивание сервера в панели «Целевые сервера»: встаёт на место <paramref name="target"/>.
    /// Порядок у серверов один — его видят панели всех вкладок и таблица на «Серверах».
    /// </summary>
    public bool MoveServer(ServerEntry dragged, ServerEntry target)
    {
        var from = Servers.IndexOf(dragged);
        var to = Servers.IndexOf(target);

        if (from < 0 || to < 0 || from == to)
        {
            return false;
        }

        // остальное перестроится по сигналу списка: наборы, панели вкладок
        Servers.Move(from, to);
        SaveServers();

        // таблица на «Серверах», отсортированная по столбцу, новый порядок не показала бы
        GridSort.Reset("servers");

        AppLog.Debug($"Сервер «{dragged.Display}» переставлен: место {from + 1} → {to + 1}.");
        return true;
    }

    public bool RemoveWorkspace(Workspace workspace)
    {
        if (workspace.IsMain)
        {
            AppLog.Hint("Первую вкладку закрыть нельзя.", LogLevel.Warning);
            return false;
        }

        if (workspace.IsBusy)
        {
            AppLog.Hint($"Вкладка «{workspace.Title}» занята операцией — сначала дождитесь её окончания.", LogLevel.Warning);
            return false;
        }

        // личная вкладка уходит вместе с вкладкой копирования — сама по себе она ничья
        if (workspace.Personal is { IsBusy: true } busy)
        {
            AppLog.Hint($"Личная вкладка «{busy.Title}» занята командой — сначала дождитесь её окончания.", LogLevel.Warning);
            return false;
        }

        var tabs = TabsOf(workspace);
        var index = tabs.IndexOf(workspace);

        if (index < 0)
        {
            return false;
        }

        if (workspace.Personal is { } personal)
        {
            if (ReferenceEquals(_activeCommand, personal))
            {
                _activeCommand = CommandTabs[0];
            }

            CommandTabs.Remove(personal);
        }

        tabs.Remove(workspace);
        workspace.StopWatching();

        // закрыли личную — у родителя свитч гаснет
        workspace.Owner?.UnlinkPersonal();

        if (ReferenceEquals(_active, workspace) || ReferenceEquals(_activeCommand, workspace)
            || ReferenceEquals(_activeDownload, workspace) || ReferenceEquals(_activeExplorer, workspace))
        {
            ActiveWorkspace = tabs[Math.Max(0, index - 1)];
        }

        SaveWorkspaces();
        AppLog.Hint($"Вкладка «{workspace.Title}» закрыта.");
        return true;
    }

    /// <summary>
    /// Ярлык — в архив или из архива обратно в основную ленту. Вкладка не меняется ничем, кроме
    /// того, в какой ленте её ярлык. Была открыта — открывается соседняя в той же ленте, а если
    /// там больше никого, вкладка остаётся открытой и лента уходит вслед за ней.
    /// </summary>
    public bool SetArchived(Workspace workspace, bool archived)
    {
        if (archived && workspace.IsMain)
        {
            AppLog.Hint("Основная вкладка в архив не убирается.", LogLevel.Warning);
            return false;
        }

        var tabs = TabsOf(workspace);
        var index = tabs.IndexOf(workspace);

        if (index < 0 || workspace.IsArchived == archived)
        {
            return false;
        }

        var wasOpen = ReferenceEquals(_active, workspace) || ReferenceEquals(_activeCommand, workspace)
            || ReferenceEquals(_activeDownload, workspace) || ReferenceEquals(_activeExplorer, workspace);

        // сначала открыть соседнюю в той же ленте, потом перенести ярлык: лента не остаётся без выбранного
        if (wasOpen
            && (tabs.Take(index).LastOrDefault(t => t.IsArchived == workspace.IsArchived)
                ?? tabs.Skip(index + 1).FirstOrDefault(t => t.IsArchived == workspace.IsArchived)) is { } next)
        {
            ActiveWorkspace = next;
        }

        workspace.IsArchived = archived;

        SaveWorkspaces();
        AppLog.Hint(archived
            ? $"«{workspace.Title}» — в архиве. Вкладка работает как прежде."
            : $"«{workspace.Title}» — снова в основной ленте.");
        ArchiveChanged?.Invoke(workspace);
        return true;
    }

    /// <summary>Ярлык перешёл между основной лентой и архивом — окно перестраивает ленту.</summary>
    public event Action<Workspace>? ArchiveChanged;

    public void SaveWorkspaces()
    {
        if (_loading || !TabsLoaded)
        {
            return;
        }

        WriteJson(AppPaths.WorkspacesFile, new WorkspacesFile
        {
            ActiveId = (_active ?? Workspaces[0]).Id,
            Workspaces = Workspaces.Select(w => w.ToState()).ToList(),
            ActiveCommandId = (_activeCommand ?? CommandTabs[0]).Id,
            CommandTabs = CommandTabs.Select(w => w.ToState()).ToList(),
            ActiveDownloadId = (_activeDownload ?? DownloadTabs[0]).Id,
            DownloadTabs = DownloadTabs.Select(w => w.ToState()).ToList(),
            ActiveExplorerId = (_activeExplorer ?? ExplorerTabs[0]).Id,
            ExplorerTabs = ExplorerTabs.Select(w => w.ToState()).ToList(),
        });
    }

    /// <summary>Вкладки всех разделов загружены — до этого сохранять и переключать нечего.</summary>
    internal bool TabsLoaded =>
        Workspaces.Count > 0 && CommandTabs.Count > 0 && DownloadTabs.Count > 0 && ExplorerTabs.Count > 0;

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

            // вкладки, сохранённые при общей галочке «Сохранять лог», берут её значение себе
            Workspace.LegacyKeepLog = Settings.KeepOutputLog;

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

            // вкладки скачивания. Файл без них — из версии, где раздела не было: одна пустая основная
            var downloadStates = file?.DownloadTabs ?? new List<WorkspaceState>();

            if (downloadStates.Count == 0)
            {
                downloadStates.Add(new WorkspaceState
                {
                    Title = "Основная",
                    IsMain = true,
                    SelectedServerIds = Workspaces[0].CopyServers.Where(c => c.IsSelected).Select(c => c.Server.Id).ToList(),
                    PreserveTimestamps = true,
                    OverwriteFiles = true,
                });
            }

            for (var i = 0; i < downloadStates.Count; i++)
            {
                var state = downloadStates[i];
                var tab = new Workspace(
                    string.IsNullOrWhiteSpace(state.Id) ? Guid.NewGuid().ToString("N") : state.Id,
                    string.IsNullOrWhiteSpace(state.Title) ? (i == 0 ? "Основная" : $"Вкладка {i + 1}") : state.Title,
                    isMain: i == 0);

                tab.SyncServers(Servers);
                tab.SelectServers(state.SelectedServerIds ?? new List<string>());
                tab.Restore(state);
                InitLegacyPane(tab, state, copying: true);

                DownloadTabs.Add(tab);
            }

            _activeDownload = DownloadTabs.FirstOrDefault(w => w.Id == file?.ActiveDownloadId) ?? DownloadTabs[0];

            // вкладки проводника. Файл без них — из версии, где раздела не было: одна основная
            // на том же сервере, что отмечен первым у первой вкладки копирования
            var explorerStates = file?.ExplorerTabs ?? new List<WorkspaceState>();

            if (explorerStates.Count == 0)
            {
                explorerStates.Add(new WorkspaceState
                {
                    Title = "Основная",
                    IsMain = true,
                    SelectedServerIds = Workspaces[0].CopyServers.Where(c => c.IsSelected).Select(c => c.Server.Id).Take(1).ToList(),
                    PreserveTimestamps = true,
                    OverwriteFiles = true,
                });
            }

            for (var i = 0; i < explorerStates.Count; i++)
            {
                var state = explorerStates[i];
                var tab = new Workspace(
                    string.IsNullOrWhiteSpace(state.Id) ? Guid.NewGuid().ToString("N") : state.Id,
                    string.IsNullOrWhiteSpace(state.Title) ? (i == 0 ? "Основная" : $"Вкладка {i + 1}") : state.Title,
                    isMain: i == 0);

                tab.SyncServers(Servers);
                tab.SelectServers(state.SelectedServerIds ?? new List<string>());
                tab.Restore(state);
                InitLegacyPane(tab, state, copying: true);

                ExplorerTabs.Add(tab);
            }

            _activeExplorer = ExplorerTabs.FirstOrDefault(w => w.Id == file?.ActiveExplorerId) ?? ExplorerTabs[0];

            // связки, у которых пропала одна сторона, распускаем: личная без родителя становится обычной
            foreach (var copy in Workspaces.Where(w => w.AfterCopyTabId.Length > 0 && w.Personal is null))
            {
                copy.UnlinkPersonal();
            }

            foreach (var orphan in CommandTabs.Where(t => t.IsPersonal && t.Owner?.Personal != t))
            {
                orphan.OwnerCopyTabId = string.Empty;
            }

            ArrangePersonalTabs();

            AppLog.Debug($"Вкладок копирования: {Workspaces.Count}, команды: {CommandTabs.Count}, "
                + $"скачивания: {DownloadTabs.Count}, проводника: {ExplorerTabs.Count}.");
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
