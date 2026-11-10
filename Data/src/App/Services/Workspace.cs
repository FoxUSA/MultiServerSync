using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using MultiServerSync.Core;
using MultiServerSync.Models;

namespace MultiServerSync.Services;

public enum WorkspaceStatus
{
    None,
    Running,
    Success,

    /// <summary>Не начато или остановлено человеком: оранжевая рамка, как у плашки. Ошибкой это не считается.</summary>
    Warning,
    Partial,
    Failed,
}

/// <summary>
/// Вкладка раздела «Копирование» или «Команда» (списки вкладок у разделов свои, см.
/// <see cref="AppState.CommandTabs"/>). Серверы берутся из выбранного набора (вкладки «Серверов»),
/// избранные команды и история путей общие, а отметки серверов, список файлов, путь на сервере,
/// текст команды и терминал у каждой вкладки свои. Командная вкладка пользуется только
/// своей частью — командой, её отметками серверов и её выводом.
/// </summary>
public sealed class Workspace : ObservableObject
{
    /// <summary>Локальные SHA-256 считаются один раз и переиспользуются всеми серверами вкладки.</summary>
    private readonly ConcurrentDictionary<string, string> _localHashes = new();

    private string _title;
    private string _lastLocalFolder = string.Empty;
    private bool _overwrite = true;
    private bool _preserveTimestamps = true;
    private bool _verifyChecksums;
    private bool _replaceWithoutAsking;
    private string _excludeMasks = string.Empty;
    private bool _useGlobalExcludes = true;
    private bool _matchByDate;
    private bool _matchByHash;
    /// <summary>Идёт восстановление из файла: правки окон не должны сохранять недочитанное.</summary>
    private bool _restoring;

    private string _statusBadge = string.Empty;
    private string _statusCaption = string.Empty;
    private string _statusTooltip = string.Empty;
    private WorkspaceStatus _status = WorkspaceStatus.None;

    private OperationBatch? _resultsBatch;

    private bool _refreshingInfo;
    private bool _refreshInfoAgain;

    /// <summary>Слежение за файлами списка: по одному наблюдателю на строку.</summary>
    private readonly Dictionary<TransferItem, ItemWatcher> _watchers = new();

    public Workspace(string id, string title, bool isMain)
    {
        Id = id;
        IsMain = isMain;
        _title = title;

        // окно есть всегда; у остальных разделов оно одно и в разметке не показывается
        _focusedWindow = NewCopyWindow("/");
        _focusedWindow.IsFocused = true;
        CopyWindows.Add(_focusedWindow);

        Steps.CollectionChanged += OnStepsChanged;
        Steps.Add(new CommandStep());

        // у прогонов вкладки — её галочка «Сохранять лог»
        foreach (var batch in OwnBatches)
        {
            batch.Owner = this;
        }
    }

    public string Id { get; }

    /// <summary>Первая вкладка. Её нельзя закрыть; в остальном она такая же, как прочие.</summary>
    public bool IsMain { get; }

    public bool CanClose => !IsMain;

    private bool _isArchived;

    /// <summary>
    /// Ярлык в архиве под «+», а не в ленте. Больше ничего не меняется: память, отметки,
    /// идущая работа, команды после копирования — всё как у вкладки в ленте.
    /// </summary>
    public bool IsArchived
    {
        get => _isArchived;
        set
        {
            if (Set(ref _isArchived, value && !IsMain))
            {
                Save();
            }
        }
    }

    public string Title
    {
        get => _title;
        set
        {
            if (Set(ref _title, string.IsNullOrWhiteSpace(value) ? "Вкладка" : value.Trim()))
            {
                // личная вкладка называется как её вкладка копирования — переименовывается вместе с ней
                if (Personal is { } personal)
                {
                    personal.FollowTitle(_title);
                }

                Save();
            }
        }
    }

    /// <summary>Галочки серверов для копирования — свои у каждой вкладки.</summary>
    public ObservableCollection<ServerCheck> CopyServers { get; } = new();

    /// <summary>Галочки серверов для команды — отдельные от копирования, тоже свои у вкладки.</summary>
    public ObservableCollection<ServerCheck> CommandServers { get; } = new();

    /// <summary>
    /// Галочки в панели «Целевые сервера»: серверы выбранного набора (вкладки раздела «Серверы»)
    /// целиком, а отметки — копирования или команды, смотря какой раздел открыт.
    /// В разделах «Серверы» и «Настройки» стоят отметки копирования.
    /// </summary>
    public IReadOnlyList<ServerCheck> Servers =>
        InActiveSet(AppState.Current.Section == AppSection.Command ? CommandServers : CopyServers);

    /// <summary>
    /// Работать можно только с серверами набора этой вкладки — других в панели не видно.
    /// На «Всех» — со всеми. Набор у каждой вкладки свой, поэтому и «Выполнить везде»
    /// идёт у каждой вкладки на её собственные серверы.
    /// </summary>
    private IReadOnlyList<ServerCheck> InActiveSet(IEnumerable<ServerCheck> set) => InSet(set, _paneSetId, _paneAllSets);

    /// <summary>Галочки из набора <paramref name="setId"/> (удалённый — первый набор); на «Всех» — все.</summary>
    private static IReadOnlyList<ServerCheck> InSet(IEnumerable<ServerCheck> set, string setId, bool allSets)
    {
        var sets = AppState.Current.ServerSets;

        if (allSets || (sets.FirstOrDefault(s => s.Id == setId) ?? sets.FirstOrDefault()) is not { } active)
        {
            return set.ToList();
        }

        return set.Where(c => active.Contains(c.Server)).ToList();
    }

    // ── панель «Целевые сервера» — своя у вкладки ─────────────────────────────────

    private string _paneSetId = string.Empty;
    private bool _paneAllSets;
    private bool _paneShowIp;
    private bool _paneShowUser;

    /// <summary>Ключ «Всех» в <see cref="_scopes"/>; у наборов ключ — их Id.</summary>
    private const string AllScope = "*";

    /// <summary>
    /// Память вкладки по каждому набору и по «Всем» отдельно: одиночный выбор, свои отметки,
    /// отметки до одиночного выбора и последний одиночный выбор. Переключение набора
    /// или свитча нигде больше ничего не трогает.
    /// </summary>
    private readonly Dictionary<string, PaneScopeState> _scopes = new(StringComparer.Ordinal);

    /// <summary>
    /// Набор серверов, из которого вкладка выбирает цели (переключатель внизу левой панели).
    /// Набор удалили — вкладка переходит на первый.
    /// </summary>
    public ServerSet? PaneSet
    {
        get
        {
            var sets = AppState.Current.ServerSets;
            return sets.FirstOrDefault(s => s.Id == _paneSetId) ?? sets.FirstOrDefault();
        }
    }

    /// <summary>У вкладки выбрано «Все»: в панели серверы всех наборов.</summary>
    public bool PaneAllSets => _paneAllSets;

    /// <summary>Галочка «IP»: под серверами в панели — адрес. Своя у вкладки.</summary>
    public bool PaneShowIp => _paneShowIp;

    /// <summary>Галочка «User»: под серверами в панели — имя пользователя. Своя у вкладки.</summary>
    public bool PaneShowUser => _paneShowUser;

    /// <summary>Одиночный выбор в наборе, который сейчас стоит у вкладки (или на «Всех»).</summary>
    public bool SingleSelect => Scope.SingleSelect;

    private string ScopeKey => _paneAllSets ? AllScope : PaneSet?.Id ?? string.Empty;

    /// <summary>
    /// Память открытого набора. Набор, в который вкладка ещё не заходила, начинает с одиночного
    /// выбора на «Команде» и без него на «Копировании» — как новые наборы раньше.
    /// </summary>
    private PaneScopeState Scope
    {
        get
        {
            var key = ScopeKey;

            if (!_scopes.TryGetValue(key, out var scope))
            {
                scope = new PaneScopeState
                {
                    Key = key,
                    SingleSelect = AppState.Current.CommandTabs.Contains(this) || AppState.Current.ExplorerTabs.Contains(this),
                };
                _scopes[key] = scope;
            }

            return scope;
        }
    }

    public void PickPaneSet(ServerSet set)
    {
        if (BlockedByLock())
        {
            return;
        }

        SwitchScope(() =>
        {
            _paneSetId = set.Id;
            _paneAllSets = false;
        });
    }

    public void PickAllSets()
    {
        if (BlockedByLock())
        {
            return;
        }

        SwitchScope(() => _paneAllSets = true);
    }

    /// <summary>
    /// «Проводник»: сервер выбирают из названия правой панели. Отметка остаётся одна — у него;
    /// сервер из другого набора переводит вкладку на тот набор.
    /// </summary>
    public void PickExplorerServer(ServerEntry server)
    {
        // под замком правая панель держит закреплённый сервер; он сам — не смена, плашка не нужна
        if (IsServerLocked(server) || BlockedByLock())
        {
            return;
        }

        if (!_paneAllSets && PaneSet is { } current && !current.Contains(server)
            && AppState.Current.ServerSets.FirstOrDefault(s => s.Contains(server)) is { } owner)
        {
            PickPaneSet(owner);
        }

        SelectServers([server.Id]);
        Save();
    }

    /// <summary>
    /// Сервер из списка у подписи в ряду разделов («Копирование», «Команда», «Скачивание»).
    /// Одиночный выбор — он становится единственным отмеченным; иначе его отметка переключается,
    /// остальные не трогаются. Сервер из другого набора — вкладка переходит на его набор. Замок держит.
    /// </summary>
    public void PickTargetServer(ServerEntry server)
    {
        if (BlockedByLock())
        {
            return;
        }

        if (!_paneAllSets && PaneSet is { } current && !current.Contains(server)
            && AppState.Current.ServerSets.FirstOrDefault(s => s.Contains(server)) is { } owner)
        {
            PickPaneSet(owner);
        }

        if (VisibleChecks().FirstOrDefault(c => c.Server.Id == server.Id) is not { } check)
        {
            return;
        }

        if (IsPaneSingleSelect)
        {
            foreach (var other in VisibleChecks().Where(c => !ReferenceEquals(c, check) && c.IsSelected))
            {
                other.IsSelected = false;
            }

            check.IsSelected = true;
        }
        else
        {
            check.IsSelected = !check.IsSelected;
        }

        Save();
    }

    /// <summary>Закреплена ли вкладка за этим сервером — для замков в списке у подписи сервера.</summary>
    public bool IsServerLocked(ServerEntry server) => _lockedIds?.Contains(server.Id) == true;

    /// <summary>
    /// Замок из списка у подписи сервера — тот же, что в строке панели слева. Сервер из другого
    /// набора сначала переводит вкладку на его набор, как и выбор из этого списка.
    /// </summary>
    public void TogglePaneLock(ServerEntry server)
    {
        if (_lockedIds is null && !_paneAllSets && PaneSet is { } current && !current.Contains(server)
            && AppState.Current.ServerSets.FirstOrDefault(s => s.Contains(server)) is { } owner)
        {
            PickPaneSet(owner);
        }

        if (VisibleChecks().FirstOrDefault(c => c.Server.Id == server.Id) is { } check)
        {
            TogglePaneLock(check);
        }
    }

    /// <summary>
    /// Смена набора: отметки уходящего запоминаются за ним, у пришедшего встают его собственные.
    /// Это нужно из-за «Всех»: там те же серверы, что в наборах, и без своей памяти отметки
    /// одного места перетирали бы отметки другого.
    /// </summary>
    private void SwitchScope(Action change)
    {
        Scope.PickedServerIds = VisibleChecks().Where(c => c.IsSelected).Select(c => c.Server.Id).ToList();

        change();

        if (Scope.PickedServerIds is { } picked)
        {
            SetVisible(picked);
        }

        OnPaneChanged();
    }

    public void SetShowIp(bool value)
    {
        _paneShowIp = value;
        Save();
    }

    public void SetShowUser(bool value)
    {
        _paneShowUser = value;
        Save();
    }

    /// <summary>Галочка «Весь журнал»: без неё журнал показывает только эту вкладку. Память своя у каждой вкладки, новая — без галочки.</summary>
    public bool JournalWhole { get; private set; }

    public void SetJournalWhole(bool value)
    {
        JournalWhole = value;
        Save();
    }

    /// <summary>Где была вкладка в избранном: открытая вкладка избранного и прокрутка списка. Своё у каждой вкладки.</summary>
    public string? BookGroup { get; private set; }

    public double BookScroll { get; private set; }

    /// <summary>В прошлый раз в избранном была открыта история команд.</summary>
    public bool BookHistory { get; private set; }

    public void SetBookPlace(string group, double scroll, bool history)
    {
        if (BookGroup == group && BookScroll == scroll && BookHistory == history)
        {
            return;
        }

        BookGroup = group;
        BookScroll = scroll;
        BookHistory = history;
        Save();
    }

    /// <summary>Сколько команд помнит история вкладки: старые уходят снизу.</summary>
    private const int CommandHistoryLimit = 500;

    private readonly ObservableCollection<string> _commandHistory = [];

    /// <summary>
    /// Выполненные во вкладке команды, свежая первой, без повторов. Своя у каждой вкладки.
    /// Наблюдаемая: строка «История» на «Команде» привязана к ней напрямую.
    /// </summary>
    public IReadOnlyList<string> CommandHistory => _commandHistory;

    /// <summary>Повтор не дублируется — поднимается наверх.</summary>
    private void RememberCommand(string text)
    {
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

        if (text.Length == 0)
        {
            return;
        }

        _commandHistory.Remove(text);
        _commandHistory.Insert(0, text);

        while (_commandHistory.Count > CommandHistoryLimit)
        {
            _commandHistory.RemoveAt(_commandHistory.Count - 1);
        }
    }

    public void ForgetCommand(string text, AppSection section)
    {
        if (!_commandHistory.Remove(text))
        {
            return;
        }

        Save();
        ActionLog.Info($"Из истории команд убрано: {Preview(text)}", null, section, this, [text]);
    }

    public void ClearCommandHistory(AppSection section)
    {
        var gone = _commandHistory.ToList();

        if (gone.Count == 0)
        {
            return;
        }

        _commandHistory.Clear();
        Save();
        ActionLog.Info($"Очищена история команд: {gone.Count}", null, section, this, gone.Select(Preview));
    }

    private List<string>? _sizeColumnsShown;

    /// <summary>Столбцы размера, выбранные на этой вкладке при исключениях; null — не выбирали. Новая вкладка — по умолчанию.</summary>
    public IReadOnlyList<string>? SizeColumnsShown => _sizeColumnsShown;

    public void SetSizeColumnsShown(List<string> shown)
    {
        _sizeColumnsShown = shown;
        Raise(nameof(SizeColumnsShown));
        Save();
    }

    /// <summary>
    /// Свитч «Одиночный выбор» в открытом наборе. Включили — отметки запоминаются, остаётся
    /// один сервер (прошлый одиночный выбор или первый отмеченный). Выключили — возвращаются
    /// ровно прежние отметки: туда-обратно ничего не стирает.
    /// </summary>
    public void SetSingleSelect(bool value)
    {
        var scope = Scope;

        if (scope.SingleSelect == value || BlockedByLock())
        {
            return;
        }

        var visible = VisibleChecks();

        if (value)
        {
            scope.MultiServerIds = visible.Where(c => c.IsSelected).Select(c => c.Server.Id).ToList();
            scope.SingleSelect = true;

            var keep = visible.FirstOrDefault(c => c.Server.Id == scope.SinglePickId)
                ?? visible.FirstOrDefault(c => c.IsSelected);

            if (keep is not null)
            {
                keep.IsSelected = true;
                KeepOnly(keep);
            }
        }
        else
        {
            scope.SinglePickId = visible.FirstOrDefault(c => c.IsSelected)?.Server.Id ?? scope.SinglePickId;
            scope.SingleSelect = false;

            if (scope.MultiServerIds is { } before)
            {
                SetVisible(before);
                scope.MultiServerIds = null;
            }
        }

        RaiseSelectionInfo();
        Save();
    }

    /// <summary>Отметки в панели — ровно эти серверы; одиночный выбор при этом не вмешивается.</summary>
    private void SetVisible(IEnumerable<string> ids)
    {
        var picked = ids.ToHashSet(StringComparer.Ordinal);
        _keepingOne = true;

        try
        {
            foreach (var check in VisibleChecks())
            {
                check.IsSelected = picked.Contains(check.Server.Id);
            }
        }
        finally
        {
            _keepingOne = false;
        }
    }

    private IReadOnlyList<ServerCheck> VisibleChecks() => InActiveSet(PaneChecks);

    /// <summary>Новая вкладка начинает с того же набора и той же памяти, что у текущей.</summary>
    public void CopyPaneFrom(Workspace source)
    {
        _paneSetId = source._paneSetId;
        _paneAllSets = source._paneAllSets;
        _paneShowIp = source._paneShowIp;
        _paneShowUser = source._paneShowUser;
        _scopes.Clear();

        foreach (var (key, scope) in source._scopes)
        {
            _scopes[key] = scope.Clone();
        }
    }

    /// <summary>
    /// Вкладка из файла прежней версии, где набор, одиночный выбор и галочки «IP»/«User» были
    /// общими: она получает то, что было выбрано. <paramref name="singleFor"/> — одиночный
    /// выбор, каким он был у каждого набора (ключ «*» — у «Всех»).
    /// </summary>
    public void InitPane(string setId, bool allSets, IReadOnlyDictionary<string, bool> singleFor, bool showIp, bool showUser)
    {
        _paneSetId = setId;
        _paneAllSets = allSets;
        _paneShowIp = showIp;
        _paneShowUser = showUser;

        foreach (var (key, single) in singleFor)
        {
            if (!_scopes.ContainsKey(key))
            {
                _scopes[key] = new PaneScopeState { Key = key, SingleSelect = single };
            }
        }
    }

    /// <summary>Вкладка из файла 2.0.8: галочки «IP» и «User» были у раздела — берём их.</summary>
    public void InitView(bool showIp, bool showUser)
    {
        _paneShowIp = showIp;
        _paneShowUser = showUser;
    }

    private void OnPaneChanged()
    {
        RaiseSection();
        RaiseCopyInfo();
        Save();
    }

    /// <summary>Список файлов окна в фокусе: с ним работают панель кнопок и таблица.</summary>
    public ObservableCollection<TransferItem> Items => _focusedWindow.Items;

    /// <summary>Окна «Копирования» или «Скачивания» сверху вниз — минимум одно.</summary>
    public ObservableCollection<CopyWindow> CopyWindows { get; } = new();

    private CopyWindow _focusedWindow;

    public CopyWindow FocusedWindow => _focusedWindow;

    /// <summary>Файлы всех окон — для счётчиков, наблюдателей и перечитывания.</summary>
    private IEnumerable<TransferItem> AllItems => CopyWindows.SelectMany(w => w.Items);

    private CopyWindow NewCopyWindow(string remotePath)
    {
        var window = new CopyWindow(this, remotePath);
        window.Items.CollectionChanged += OnItemsChanged;
        window.Downloads.CollectionChanged += OnDownloadsChanged;
        return window;
    }

    /// <summary>Правка в окне: сохранить вкладку. Само окно вкладку не знает — только просит.</summary>
    public void SaveWindow() => Save();

    /// <summary>Значения окна в фокусе лежат в полях вкладки, которые показывают панель и параметры.</summary>
    private CopyOptions CurrentCopyOptions => new(
        _overwrite, _preserveTimestamps, _verifyChecksums, _matchByDate, _matchByHash, _excludeMasks, _useGlobalExcludes);

    private CopyOptions OptionsOf(CopyWindow window) =>
        ReferenceEquals(window, _focusedWindow) ? CurrentCopyOptions : window.Options ?? CurrentCopyOptions;

    /// <summary>Маски окна: у окна в фокусе — в полях вкладки, у остальных — в его параметрах.</summary>
    public string MasksOf(CopyWindow window) => OptionsOf(window).ExcludeMasks;

    public bool GlobalExcludesOf(CopyWindow window) => OptionsOf(window).UseGlobalExcludes;

    /// <summary>Новые маски окна — без перевода фокуса на него: фильтр «Проводника» пишет в любое окно.</summary>
    public void SetMasksOf(CopyWindow window, string masks)
    {
        if (ReferenceEquals(window, _focusedWindow))
        {
            ExcludeMasks = masks;
            return;
        }

        if (OptionsOf(window).ExcludeMasks == masks)
        {
            return;
        }

        window.Options = OptionsOf(window) with { ExcludeMasks = masks };
        Save();
        RemeasureKept();
        Raise(nameof(AnyWindowFilters));
    }

    /// <summary>Исключения есть хоть у одного окна: шапка общая, и столбцы со звёздочкой — у всех окон.</summary>
    public bool AnyWindowFilters => CopyWindows.Any(w => !ExcludesOf(w).IsEmpty);

    // ── целевые серверы у каждого окна копирования ─────────────────────────────

    /// <summary>Галочки слева меняются местами со сменой окна в фокусе: их сигнал — не правка человека.</summary>
    private bool _swappingChecks;

    /// <summary>
    /// Кнопка «Разные сервера» на «Копировании»: у каждого окна свои серверы, набор и одиночный выбор, номера окон
    /// в панели слева. По умолчанию выключена — все окна идут на отмеченные слева. Окно одно — режим гаснет сам.
    /// </summary>
    private bool _separateServers;

    public bool SeparateServers => _separateServers;

    /// <summary>Надпись кнопки — что она сделает: включить разные серверы или вернуть общие.</summary>
    public string SeparateServersCaption => _separateServers ? "Один сервер" : "Разные сервера";

    /// <summary>Серверы свои у каждого окна только на «Копировании» и с кнопкой: на «Скачивании» сервер знает сама строка.</summary>
    private bool WindowServers => _separateServers && !IsDownloadTab;

    /// <summary>
    /// Включить или выключить разные серверы. Память окон не стирается: выключили — все идут на серверы окна #1
    /// (сказано), включили снова — общие отметки остаются у #1, у остальных окон встают их прежние.
    /// Меняет, куда пойдёт копирование, — замок держит.
    /// </summary>
    public void ToggleSeparateServers()
    {
        if (IsDownloadTab || (!_separateServers && CopyWindows.Count < 2) || BlockedByLock())
        {
            return;
        }

        SetHintSeparate(false);

        var first = CopyWindows[0];

        if (_separateServers)
        {
            _focusedWindow.ServerIds = LiveCopyIds();
            _focusedWindow.Pane = PaneNow();

            // общими становятся серверы окна #1
            if (!ReferenceEquals(first, _focusedWindow) && first.ServerIds is { } ids && first.Pane is { } pane)
            {
                PutChecks(ids, pane, raise: true);
            }
        }
        else
        {
            var common = LiveCopyIds();
            var commonPane = PaneNow();
            first.ServerIds = common;
            first.Pane = commonPane;

            // окно без своей памяти начинает с общих
            foreach (var window in CopyWindows.Where(w => w.ServerIds is null))
            {
                window.ServerIds = common.ToList();
                window.Pane = commonPane.Copy();
            }

            // в панели — окно в фокусе со своими
            if (!ReferenceEquals(first, _focusedWindow) && _focusedWindow is { ServerIds: { } own, Pane: { } ownPane })
            {
                PutChecks(own, ownPane, raise: true);
            }
        }

        SetSeparateServers(!_separateServers);
        Save();
    }

    private void SetSeparateServers(bool value)
    {
        if (_separateServers == value)
        {
            return;
        }

        _separateServers = value;
        RaiseMany(nameof(SeparateServers), nameof(SeparateServersCaption));
        RaiseCopyInfo();
    }

    private List<string> LiveCopyIds() => CopyServers.Where(c => c.IsSelected).Select(c => c.Server.Id).ToList();

    /// <summary>Отмеченные серверы окна: у окна в фокусе — галочки слева, у остальных — его память.</summary>
    private IReadOnlyList<string> ServerIdsOf(CopyWindow window) =>
        !WindowServers || ReferenceEquals(window, _focusedWindow) ? LiveCopyIds() : window.ServerIds ?? LiveCopyIds();

    /// <summary>Куда идёт окно: его отмеченные серверы в открытом наборе (или во «Всех»).</summary>
    public IReadOnlyList<ServerEntry> TargetsOf(CopyWindow window)
    {
        if (!WindowServers || ReferenceEquals(window, _focusedWindow))
        {
            return CopyTargets;
        }

        var ids = ServerIdsOf(window).ToHashSet(StringComparer.Ordinal);
        var pane = PaneOf(window);
        return InSet(CopyServers, pane.SetId, pane.AllSets).Where(c => ids.Contains(c.Server.Id)).Select(c => c.Server).ToList();
    }

    /// <summary>Набор и его память, что стоят в панели сейчас — у окна в фокусе.</summary>
    private WindowPane PaneNow() => new(_paneSetId, _paneAllSets, _scopes.Values.Select(s => s.Clone()).ToList());

    /// <summary>Память окна для файла: пишется и при выключенных «Разных серверах», чтобы включение их вернуло.</summary>
    private List<string> StoredIdsOf(CopyWindow window) =>
        ReferenceEquals(window, _focusedWindow) || window.ServerIds is null ? LiveCopyIds() : window.ServerIds.ToList();

    private WindowPane StoredPaneOf(CopyWindow window) =>
        ReferenceEquals(window, _focusedWindow) || window.Pane is null ? PaneNow() : window.Pane.Copy();

    /// <summary>Набор окна: у окна в фокусе — панель слева, у остальных — его память.</summary>
    private WindowPane PaneOf(CopyWindow window) =>
        !WindowServers || ReferenceEquals(window, _focusedWindow) ? PaneNow() : window.Pane ?? PaneNow();

    private void LoadPane(WindowPane pane)
    {
        _paneSetId = pane.SetId;
        _paneAllSets = pane.AllSets;
        _scopes.Clear();

        foreach (var scope in pane.Scopes)
        {
            _scopes[scope.Key] = scope.Clone();
        }
    }

    private static WindowPane? PaneFrom(SavedCopyWindow saved) =>
        saved.PaneScopes is { } scopes ? new(saved.PaneSetId ?? string.Empty, saved.PaneAllSets, scopes.Select(s => s.Clone()).ToList()) : null;

    /// <summary>Строка операции окна пойдёт на этот сервер. Без окна (из «Проводника») — на все переданные.</summary>
    private bool Goes(UploadJob job, ServerEntry server) => job.Window is not { } window || TargetsOf(window).Contains(server);

    /// <summary>
    /// Панель слева — окна, которое встало в фокус: его галочки, набор и свитч одиночного выбора;
    /// прежнее уносит свои с собой. <paramref name="raise"/> — панель перечитывается (не при загрузке).
    /// </summary>
    private void SwapChecks(CopyWindow from, CopyWindow to, bool raise = true)
    {
        if (!WindowServers || ReferenceEquals(from, to))
        {
            return;
        }

        from.ServerIds = LiveCopyIds();
        from.Pane = PaneNow();
        PutChecks(to.ServerIds ?? from.ServerIds, to.Pane ?? from.Pane, raise);
    }

    /// <summary>В панель слева — эти галочки, набор и свитч; сигнал галочек — не правка человека.</summary>
    private void PutChecks(IEnumerable<string> serverIds, WindowPane pane, bool raise)
    {
        LoadPane(pane);
        var ids = serverIds.ToHashSet(StringComparer.Ordinal);
        _swappingChecks = true;

        try
        {
            foreach (var check in CopyServers)
            {
                var on = ids.Contains(check.Server.Id);

                if (check.IsSelected != on)
                {
                    check.IsSelected = on;
                }
            }
        }
        finally
        {
            _swappingChecks = false;
        }

        RaiseSelectionInfo();

        if (raise)
        {
            AppState.Current.RefreshPane(this);
        }
    }

    /// <summary>Номер окна в панели серверов нажали — таблица показывает его строки.</summary>
    public event Action<CopyWindow>? WindowShowRequested;

    /// <summary>Номер окна в панели серверов: фокус на окне, галочки слева — его серверы.</summary>
    public void ShowWindow(CopyWindow window)
    {
        FocusWindow(window);
        WindowShowRequested?.Invoke(window);
    }

    private void LoadCopyOptions(CopyOptions options)
    {
        _overwrite = options.Overwrite;
        _preserveTimestamps = options.PreserveTimestamps;
        _verifyChecksums = options.VerifyChecksums;
        _matchByDate = options.MatchByDate;
        _matchByHash = options.MatchByHash;
        _excludeMasks = options.ExcludeMasks;
        _useGlobalExcludes = options.UseGlobalExcludes;
    }

    /// <summary>Щелчок в окне: панель кнопок и параметры переходят на него.</summary>
    public void FocusWindow(CopyWindow window)
    {
        if (ReferenceEquals(window, _focusedWindow) || !CopyWindows.Contains(window))
        {
            return;
        }

        var from = _focusedWindow;
        _focusedWindow.Options = CurrentCopyOptions;
        _focusedWindow.IsFocused = false;
        _focusedWindow = window;
        window.IsFocused = true;
        LoadCopyOptions(window.Options ?? CurrentCopyOptions);
        SwapChecks(from, window);

        RaiseCopyOptions();
        RaiseCopyInfo();
        Raise(nameof(FocusedWindow));
        Save();
    }

    private void RaiseCopyOptions() => RaiseMany(
        nameof(RemotePath),
        nameof(Items),
        nameof(Downloads),
        nameof(LocalFolder),
        nameof(DownloadInfo),
        nameof(AllDownloadsSelected),
        nameof(OverwriteFiles),
        nameof(PreserveTimestamps),
        nameof(VerifyChecksums),
        nameof(ExcludeMasks),
        nameof(HasExcludes),
        nameof(HasGlobalExcludes),
        nameof(UseGlobalExcludes),
        nameof(MatchByDate),
        nameof(MatchByHash),
        nameof(SendEverything),
        nameof(SendMissingOnly),
        nameof(SendByDate),
        nameof(SendByHash));

    public bool IsDownloadTab => AppState.Current.DownloadTabs.Contains(this);

    /// <summary>Новое пустое окно под <paramref name="after"/>: параметры как у него, список, путь и свои исключения пустые.</summary>
    public CopyWindow AddCopyWindow(CopyWindow after)
    {
        var options = OptionsOf(after);
        var window = NewCopyWindow(string.Empty);
        window.Options = options with { ExcludeMasks = string.Empty };

        // серверы, набор и одиночный выбор — как у окна #1: новое окно идёт туда же, пока ему не отметили свои
        window.ServerIds = ServerIdsOf(CopyWindows[0]).ToList();
        window.Pane = PaneOf(CopyWindows[0]).Copy();

        CopyWindows.Insert(CopyWindows.IndexOf(after) + 1, window);
        RenumberWindows();
        FocusWindow(window);
        Save();

        // появилась кнопка «Разные сервера» — подсвечена, пока человек не уйдёт с вкладки
        if (CopyWindows.Count == 2 && !IsDownloadTab && !_separateServers)
        {
            SetHintSeparate(true);
        }

        ActionLog.Info($"Добавлено окно {(IsDownloadTab ? "скачивания" : "копирования")} #{window.Number} · окон {CopyWindows.Count}",
            null, IsDownloadTab ? AppSection.Download : AppSection.Files, this);
        return window;
    }

    /// <summary>Убрать окно; последнее остаётся. Фокус — на соседа.</summary>
    public void RemoveCopyWindow(CopyWindow window)
    {
        var index = CopyWindows.IndexOf(window);

        if (index < 0 || CopyWindows.Count == 1)
        {
            return;
        }

        var number = window.Number;
        var gone = window.Items.Select(i => i.FullPath)
            .Concat(window.Downloads.Select(d => $"{d.ServerLabel}: {d.Path}"))
            .ToList();

        if (ReferenceEquals(window, _focusedWindow))
        {
            FocusWindow(CopyWindows[index == CopyWindows.Count - 1 ? index - 1 : index + 1]);
        }

        window.Items.Clear();
        window.Items.CollectionChanged -= OnItemsChanged;
        window.Downloads.Clear();
        window.Downloads.CollectionChanged -= OnDownloadsChanged;
        window.Detach();
        CopyWindows.Remove(window);
        RenumberWindows();
        RaiseCopyInfo();
        RaiseDownloadInfo();
        Save();

        ActionLog.Info($"Убрано окно {(IsDownloadTab ? "скачивания" : "копирования")} #{number} · окон {CopyWindows.Count}",
            null, IsDownloadTab ? AppSection.Download : AppSection.Files, this, gone);
    }

    /// <summary>Окно перетащили: номера идут заново, а по ним и порядок запуска.</summary>
    public void MoveCopyWindow(CopyWindow window, int to)
    {
        var from = CopyWindows.IndexOf(window);

        if (from < 0 || from == to || to < 0 || to >= CopyWindows.Count)
        {
            return;
        }

        CopyWindows.Move(from, to);
        RenumberWindows();
        RaiseCopyInfo();
        RaiseDownloadInfo();
        Save();

        ActionLog.Info($"Окно {(IsDownloadTab ? "скачивания" : "копирования")} переставлено: #{from + 1} → #{to + 1}",
            null, IsDownloadTab ? AppSection.Download : AppSection.Files, this);
    }

    private void RenumberWindows()
    {
        for (var i = 0; i < CopyWindows.Count; i++)
        {
            var window = CopyWindows[i];
            window.Number = i + 1;
            window.IsMulti = CopyWindows.Count > 1;
            window.IsLast = i == CopyWindows.Count - 1;
        }

        if (CopyWindows.Count < 2)
        {
            SetSeparateServers(false);
            SetHintSeparate(false);
        }

        Raise(nameof(IsMultiWindow));
    }

    private bool _hintSeparate;

    /// <summary>Кнопка «Разные сервера» только что появилась: оранжевая, пока человек не уйдёт с вкладки или не нажмёт её.</summary>
    public bool HintSeparateServers => _hintSeparate;

    private void SetHintSeparate(bool on)
    {
        if (_hintSeparate == on)
        {
            return;
        }

        _hintSeparate = on;
        Raise(nameof(HintSeparateServers));

        if (on)
        {
            AppState.Current.ActiveWorkspaceChanged += LeaveHint;
        }
        else
        {
            AppState.Current.ActiveWorkspaceChanged -= LeaveHint;
        }
    }

    private void LeaveHint(Workspace now)
    {
        if (!ReferenceEquals(now, this))
        {
            SetHintSeparate(false);
        }
    }

    private bool _showWindowNames;

    /// <summary>Окон больше одного: на панели видна кнопка «Название».</summary>
    public bool IsMultiWindow => CopyWindows.Count > 1;

    /// <summary>Кнопка «Название»: в окнах вкладки видны плашки названий.</summary>
    public bool ShowWindowNames
    {
        get => _showWindowNames;
        set { if (Set(ref _showWindowNames, value)) { Save(); } }
    }

    private const int WindowNameLimit = 30;

    /// <summary>Двойной щелчок по плашке. Дальше предела шрифт уже не ужать, не обрезав надпись.</summary>
    public void RenameWindow(CopyWindow window, string raw)
    {
        var name = raw.Trim();

        if (name == window.Name)
        {
            return;
        }

        if (name.Length > WindowNameLimit)
        {
            AppLog.Hint($"Название длиннее {WindowNameLimit} символов не поместится на плашке.", LogLevel.Warning);
            return;
        }

        window.Name = name;
        ActionLog.Info($"Окно {window.NumberText}: название «{window.NameText}»", null, IsDownloadTab ? AppSection.Download : AppSection.Files, this);
    }

    /// <summary>
    /// История путей поменялась: выпадающий список чистит поле, когда из него пропадает выбранная
    /// строка, а поле у каждого окна своё. Пути окон ставятся обратно после правки истории.
    /// </summary>
    public void KeepWindowPaths(Action change)
    {
        var paths = CopyWindows.Select(w => (Window: w, w.RemotePath)).ToList();

        change();

        foreach (var (window, path) in paths)
        {
            window.RemotePath = path;
        }
    }

    public OperationBatch CopyBatch { get; } = new();

    public OperationBatch CommandBatch { get; } = new();

    public OperationBatch CheckBatch { get; } = new();

    /// <summary>Сводка по всем вкладкам — заполняется на той вкладке, с которой нажали «Выполнить везде».</summary>
    public OperationBatch SummaryBatch { get; } = new();

    /// <summary>«Сравнить» в проводнике: подробный отчёт — в терминал вкладки, а не плашкой.</summary>
    public OperationBatch CompareBatch { get; } = new();

    private IEnumerable<OperationBatch> OwnBatches => [CopyBatch, CommandBatch, CheckBatch, SummaryBatch, CompareBatch];

    /// <summary>Прогоны вкладки, что пишутся на диск, по ключам файла. Сводка — из чужих строк, её нет.</summary>
    private IEnumerable<(string Key, OperationBatch Batch)> StoredBatches =>
        [("copy", CopyBatch), ("command", CommandBatch), ("check", CheckBatch), ("compare", CompareBatch)];

    /// <summary>Терминал вкладки для UserData\terminal.json; пустой — null.</summary>
    internal TerminalTabState? TerminalState()
    {
        var state = new TerminalTabState();

        foreach (var (key, batch) in StoredBatches)
        {
            if (batch.ToState() is { } saved)
            {
                state.Batches[key] = saved;
            }

            if (ReferenceEquals(_resultsBatch, batch))
            {
                state.Shown = key;
            }
        }

        return state.Batches.Count == 0 ? null : state;
    }

    /// <summary>Терминал вкладки после перезапуска — те же строки, тот же показанный прогон.</summary>
    internal void RestoreTerminal(TerminalTabState state, Func<string, ServerEntry?> server, DateTime savedAt)
    {
        foreach (var (key, batch) in StoredBatches)
        {
            if (state.Batches.TryGetValue(key, out var saved))
            {
                batch.Restore(saved, server, savedAt);
            }

            if (key == state.Shown && batch.HasResults)
            {
                ShowResults(batch);
            }
        }
    }

    /// <summary>Отсчёт срока жизни вывода у всех прогонов вкладки.</summary>
    internal void TickOutputLife()
    {
        foreach (var batch in OwnBatches)
        {
            batch.TickLife();
        }
    }

    /// <summary>Общая галочка «Сохранять лог» прежних версий — для вкладок, сохранённых до того, как она стала своей.</summary>
    internal static bool LegacyKeepLog { get; set; }

    private bool _keepOutputLog;

    /// <summary>
    /// «Сохранять лог» этой вкладки: отмечено — терминал вкладки хранит все прогоны, пока галочку
    /// не снимут; снято — прошлые строки уходят после срока жизни вывода.
    /// </summary>
    public bool KeepOutputLog
    {
        get => _keepOutputLog;
        set
        {
            if (Set(ref _keepOutputLog, value))
            {
                foreach (var batch in OwnBatches)
                {
                    batch.RaiseKeepLog();
                }

                Save();
            }
        }
    }

    // ── «авто», «*.txt» и CopyPass в строке терминала — свои у вкладки ─────────

    private bool _autoCopy;
    private bool _logAsFile;
    private bool _copyPass;

    /// <summary>«Авто»: по окончании операции вкладки вывод её серверов сам уходит в буфер. Новая вкладка — без неё.</summary>
    public bool AutoCopy => _autoCopy;

    /// <summary>«*.txt»: длинный лог автокопии — файлом с полным логом, а не обрезкой.</summary>
    public bool LogAsFile => _logAsFile;

    /// <summary>CopyPass: лог автокопии сам вставится в первое поле ввода.</summary>
    public bool CopyPass => _copyPass;

    /// <returns>Галочка изменилась.</returns>
    public bool SetAutoCopy(bool value) => SetTerminalCheck(ref _autoCopy, value);

    /// <returns>Галочка изменилась.</returns>
    public bool SetLogAsFile(bool value) => SetTerminalCheck(ref _logAsFile, value);

    /// <returns>Галочка изменилась.</returns>
    public bool SetCopyPass(bool value) => SetTerminalCheck(ref _copyPass, value);

    private bool SetTerminalCheck(ref bool field, bool value)
    {
        if (field == value)
        {
            return false;
        }

        field = value;
        Save();
        return true;
    }

    /// <summary>Вкладка из файла прежней версии, где эти галочки были у раздела: берёт галочки своего раздела.</summary>
    public void InitTerminalChecks(bool autoCopy, bool logAsFile, bool copyPass)
    {
        _autoCopy = autoCopy;
        _logAsFile = logAsFile;
        _copyPass = copyPass;
    }

    /// <summary>
    /// Сравнение двух файлов проводника строкой сервера в терминале: <paramref name="work"/>
    /// пишет в вывод что с чем сравнивается и итог.
    /// </summary>
    public async Task CompareAsync(ServerEntry server, Func<OperationResult, CancellationToken, Task> work)
    {
        AppState.Current.RequestTerminal();
        ShowResults(CompareBatch);
        CompareBatch.Scope = AppState.Current.TabsOf(this).Count > 1 ? Title : null;
        CompareBatch.Section = AppSection.Explorer;
        await CompareBatch.RunAsync("Сравнение файлов", [server], (_, result, ct) => work(result, ct), maxParallel: 1);
    }

    /// <summary>Сравнение в «Проводнике» — всегда по содержимому, что бы ни стояло в параметрах; исключения те же.</summary>
    private UploadOptions ByContent => CurrentOptions with { Overwrite = true, MatchByDate = false, MatchByHash = true };

    /// <summary>Файлы и папки компьютера против одноимённых в папке сервера: та же сверка, что у «Сравнить» в «Копировании».</summary>
    public async Task CompareLocalAsync(ServerEntry server, IReadOnlyList<string> paths, string remoteDir, OperationResult result, CancellationToken ct)
    {
        result.Append(BlockParams);
        result.Append($"Сравнение с {remoteDir} — ничего не копируется");
        result.Append($"объектов: {paths.Count}");
        result.Append("── сравнение ──");

        var items = await Task.Run(() => paths.Select(p => new TransferItem(p)).ToList(), ct);
        var stats = await SshService.CompareUploadAsync(server, AppState.Current.Settings, items, remoteDir, ByContent, _localHashes, line => result.Append(line), ct);

        ReportCompare(result, stats, "на сервере");
    }

    /// <summary>Файлы и папки сервера против одноимённых в папке компьютера: та же сверка, что у «Сравнить» в «Скачивании».</summary>
    public async Task CompareRemoteAsync(ServerEntry server, IReadOnlyList<string> paths, string localDir, OperationResult result, CancellationToken ct)
    {
        result.Append(BlockParams);
        result.Append($"Сравнение с {localDir} — ничего не скачивается");
        result.Append($"путей: {paths.Count}");
        result.Append("── сравнение ──");

        var stats = await DownloadService.CompareAsync(server, AppState.Current.Settings, paths, localDir, ByContent, line => result.Append(line), ct);

        ReportCompare(result, stats, "здесь");
    }

    /// <summary>Что показывает терминал внизу окна: результат последней запущенной операции.</summary>
    public OperationBatch ResultsBatch => _resultsBatch ?? CommandBatch;

    /// <summary>Копирование и команды мини-терминала идут одним списком в терминале вкладки: команда копирование из него не вытесняет.</summary>
    internal OperationBatch? CompanionOf(OperationBatch batch)
    {
        if (AppState.Current.SectionOfTab(this) is not (AppSection.Files or AppSection.Download or AppSection.Explorer))
        {
            return null;
        }

        return ReferenceEquals(batch, CopyBatch) ? CommandBatch
            : ReferenceEquals(batch, CommandBatch) ? CopyBatch
            : null;
    }

    /// <summary>
    /// «Выполнить везде»: терминал нажавшей вкладки показывает строки всех вкладок сразу,
    /// чтобы отчёт был в одном месте, а не по своим углам.
    /// </summary>
    public void ShowSummary(IReadOnlyList<OperationBatch> sources, string what)
    {
        SummaryBatch.TrackAll(sources, what);

        _resultsBatch = SummaryBatch;
        Raise(nameof(ResultsBatch));

        // на ярлык сводка не идёт: у первой вкладки на плашке только её собственная работа,
        // а общий отчёт по всем вкладкам — в её терминале
    }

    public string RemotePath
    {
        get => _focusedWindow.RemotePath;
        set => _focusedWindow.RemotePath = value;
    }

    /// <summary>Откуда в эту вкладку последний раз добавляли файлы — там и откроется проводник.</summary>
    public string LastLocalFolder
    {
        get => _lastLocalFolder;
        set { if (Set(ref _lastLocalFolder, value ?? string.Empty)) { Save(); } }
    }

    public bool OverwriteFiles
    {
        get => _overwrite;
        set { if (Set(ref _overwrite, value)) { SpreadOptions(o => o with { Overwrite = value }); RaiseMode(); Save(); } }
    }

    public bool PreserveTimestamps
    {
        get => _preserveTimestamps;
        set { if (Set(ref _preserveTimestamps, value)) { SpreadOptions(o => o with { PreserveTimestamps = value }); Save(); } }
    }

    public bool VerifyChecksums
    {
        get => _verifyChecksums;
        set { if (Set(ref _verifyChecksums, value)) { SpreadOptions(o => o with { VerifyChecksums = value }); Save(); } }
    }

    /// <summary>
    /// В шапке таблицы отмечено всё, во всех окнах — выбор в параметрах копирования ложится во все окна вкладки,
    /// а не только в окно в фокусе. Маски у окон свои и сюда не входят.
    /// </summary>
    private void SpreadOptions(Func<CopyOptions, CopyOptions> change)
    {
        var all = IsDownloadTab ? AllWindowsDownloadsSelected : AllWindowsItemsSelected;

        if (CopyWindows.Count < 2 || all != true)
        {
            return;
        }

        foreach (var window in CopyWindows.Where(w => !ReferenceEquals(w, _focusedWindow)))
        {
            window.Options = change(OptionsOf(window));
        }
    }

    /// <summary>
    /// «Проводник»: заменять файлы в назначении, не спрашивая. По умолчанию копирование,
    /// которое перезапишет файлы, сначала спрашивает.
    /// </summary>
    public bool ReplaceWithoutAsking
    {
        get => _replaceWithoutAsking;
        set { if (Set(ref _replaceWithoutAsking, value)) { Save(); } }
    }

    /// <summary>Исключения при копировании: маски по одной в строке.</summary>
    public string ExcludeMasks
    {
        get => _excludeMasks;
        set { if (Set(ref _excludeMasks, value ?? string.Empty)) { Raise(nameof(HasExcludes)); Save(); RemeasureKept(); } }
    }

    /// <summary>Свои исключения или галочки «Проводника» заданы — значок кнопки на зелёной плашке: что-то из списка не поедет.</summary>
    public bool HasExcludes => ExcludeRules.Count(_excludeMasks) > 0 || ExplorerPicks.Parse(_excludeMasks).HasAny;

    /// <summary>
    /// Вкладка «Проводника», открытая как фильтр этой вкладки галочками; null — обычная вкладка.
    /// Живёт, пока её не покинули, и в файл вкладок не пишется: сам фильтр — в масках окна.
    /// </summary>
    public ExplorerFilter? FilterOf { get; set; }

    /// <summary>Глобальные исключения действуют и здесь — переключатель на вкладке «Глобально» окна исключений.</summary>
    public bool UseGlobalExcludes
    {
        get => _useGlobalExcludes;
        set { if (Set(ref _useGlobalExcludes, value)) { Raise(nameof(HasGlobalExcludes)); Save(); RemeasureKept(); } }
    }

    /// <summary>Глобальные исключения заданы и здесь не выключены — без своих значок на фиолетовой плашке.</summary>
    public bool HasGlobalExcludes => _useGlobalExcludes && ExcludeRules.Count(AppState.Current.GlobalExcludeMasks) > 0;

    /// <summary>Глобальные маски сменились. К серверам идёт только открытая вкладка: остальные спросят сами, когда их откроют.</summary>
    public void RaiseGlobalExcludes()
    {
        Raise(nameof(HasGlobalExcludes));
        RemeasureKept(servers: ReferenceEquals(AppState.Current.ActiveWorkspace, this));
    }

    /// <summary>Исключения окна — те же, что у настоящего захода.</summary>
    public ExcludeFilter ExcludesOf(CopyWindow window) => UploadOptionsOf(window).Excludes();

    /// <summary>Маски сменились — «Размер*» и «Файлов*» заново. Идёт проверка — повторится сразу после неё: она считала по старым.</summary>
    private void RemeasureKept(bool servers = true)
    {
        _refreshInfoAgain = _refreshingInfo;
        _ = RefreshItemsInfoAsync();

        if (servers)
        {
            _checkDownloadsAgain = _checkingDownloads;
            _ = RefreshDownloadsAsync();
        }
    }

    /// <summary>Глобальные маски для захода — пусто, если на этой вкладке они выключены.</summary>
    private string GlobalExcludeMasks => _useGlobalExcludes ? AppState.Current.GlobalExcludeMasks : string.Empty;

    /// <summary>Заменять файлы с другой датой изменения — в любую сторону, а не только старее.</summary>
    public bool MatchByDate
    {
        get => _matchByDate;
        set { if (Set(ref _matchByDate, value)) { SpreadOptions(o => o with { MatchByDate = value }); RaiseMode(); Save(); } }
    }

    /// <summary>Заменять файлы с другой контрольной суммой: решение по содержимому, а не по дате.</summary>
    public bool MatchByHash
    {
        get => _matchByHash;
        set { if (Set(ref _matchByHash, value)) { SpreadOptions(o => o with { MatchByHash = value }); RaiseMode(); Save(); } }
    }

    // ── что отправлять: один выбор из четырёх ──────────────────────────────────
    //
    // Раньше это были три независимые галочки, но независимыми они никогда не были:
    // «не перезаписывать» отменяло любую сверку, а сверка по сумме — сверку по дате.
    // Взаимное исключение теперь видно глазами, а не выясняется по выводу.

    /// <summary>Слать всё подряд, ничего не сверяя.</summary>
    public bool SendEverything
    {
        get => _overwrite && !_matchByDate && !_matchByHash;
        set { if (value) { SetMode(overwrite: true, byDate: false, byHash: false); } }
    }

    /// <summary>Существующие файлы не трогать совсем.</summary>
    public bool SendMissingOnly
    {
        get => !_overwrite;
        set { if (value) { SetMode(overwrite: false, byDate: false, byHash: false); } }
    }

    /// <summary>Слать то, у чего разошлась дата изменения (размер сверяется всегда).</summary>
    public bool SendByDate
    {
        get => _overwrite && _matchByDate && !_matchByHash;
        set { if (value) { SetMode(overwrite: true, byDate: true, byHash: false); } }
    }

    /// <summary>Слать то, у чего разошлось содержимое.</summary>
    public bool SendByHash
    {
        get => _overwrite && _matchByHash;
        set { if (value) { SetMode(overwrite: true, byDate: false, byHash: true); } }
    }

    private void SetMode(bool overwrite, bool byDate, bool byHash)
    {
        _overwrite = overwrite;
        _matchByDate = byDate;
        _matchByHash = byHash;
        SpreadOptions(o => o with { Overwrite = overwrite, MatchByDate = byDate, MatchByHash = byHash });

        RaiseMany(nameof(OverwriteFiles), nameof(MatchByDate), nameof(MatchByHash));
        RaiseMode();
        Save();
    }

    private void RaiseMode() => RaiseMany(
        nameof(SendEverything),
        nameof(SendMissingOnly),
        nameof(SendByDate),
        nameof(SendByHash));

    // ── окна команды (многозадачность) ─────────────────────────────────────────

    /// <summary>
    /// Окна команды по порядку — минимум одно. Цепочкой: следующее окно начинается на сервере
    /// только после успешного предыдущего на этом же сервере.
    /// </summary>
    public ObservableCollection<CommandStep> Steps { get; } = new();

    /// <summary>
    /// Личная командная вкладка вкладки копирования: id родителя. Пусто — обычная вкладка.
    /// Личная выполняется после удачного копирования родителя и в «Выполнить везде» не идёт.
    /// </summary>
    public string OwnerCopyTabId { get; set; } = string.Empty;

    public bool IsPersonal => OwnerCopyTabId.Length > 0;

    /// <summary>Вкладка копирования, чья это личная вкладка.</summary>
    public Workspace? Owner => IsPersonal
        ? AppState.Current.Workspaces.Concat(AppState.Current.DownloadTabs).FirstOrDefault(w => w.Id == OwnerCopyTabId)
        : null;

    /// <summary>Название личной вкладки — всегда как у родителя, руками не меняется.</summary>
    public void FollowTitle(string title)
    {
        if (Set(ref _title, title, nameof(Title)))
        {
            Save();
        }
    }

    // ── скачивание ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Что скачивать в окне в фокусе: файлы и папки, каждая строка со своего сервера — их выбирают
    /// обзором сервера. Качаются строки с галочкой; отметки серверов слева здесь не нужны.
    /// </summary>
    public ObservableCollection<DownloadItem> Downloads => _focusedWindow.Downloads;

    /// <summary>Строки всех окон — для счётчиков и проверки списка.</summary>
    private IEnumerable<DownloadItem> AllDownloads => CopyWindows.SelectMany(w => w.Downloads);

    private IEnumerable<DownloadItem> PickedDownloads => AllDownloads.Where(d => d.IsSelected);

    private void OnDownloadsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var item in e.OldItems?.OfType<DownloadItem>() ?? [])
        {
            item.PropertyChanged -= OnDownloadPicked;
        }

        foreach (var item in e.NewItems?.OfType<DownloadItem>() ?? [])
        {
            item.PropertyChanged += OnDownloadPicked;
        }

        RaiseDownloadInfo();
    }

    private void OnDownloadPicked(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DownloadItem.IsSelected))
        {
            RaiseDownloadInfo();
        }
    }

    private void RaiseDownloadInfo()
    {
        foreach (var window in CopyWindows)
        {
            window.RaiseDownloads();
        }

        RaiseMany(nameof(UnsentOn), nameof(DownloadInfo), nameof(AllDownloadsSelected), nameof(AllWindowsDownloadsSelected), nameof(TabCounts), nameof(TabTooltip));

        if (!_restoring)
        {
            Save();
        }
    }

    /// <summary>Галочка в шапке таблицы скачивания: все / ничего / частично.</summary>
    public bool? AllDownloadsSelected => TriState(Downloads.Count, Downloads.Count(d => d.IsSelected));

    public void SelectAllDownloads(bool selectAll)
    {
        foreach (var item in Downloads)
        {
            item.IsSelected = selectAll;
        }
    }

    /// <summary>Галочка в общей шапке таблицы: строки всех окон.</summary>
    public bool? AllWindowsDownloadsSelected => TriState(AllDownloads.Count(), AllDownloads.Count(d => d.IsSelected));

    public void SelectAllWindowsDownloads(bool selectAll)
    {
        foreach (var item in AllDownloads)
        {
            item.IsSelected = selectAll;
        }
    }

    /// <summary>Куда скачивать из окна в фокусе. С нескольких серверов — каждый в свою подпапку по имени сервера.</summary>
    public string LocalFolder
    {
        get => _focusedWindow.LocalFolder;
        set => _focusedWindow.LocalFolder = value;
    }

    public string DownloadInfo
    {
        get
        {
            var picked = Downloads.Where(d => d.IsSelected).ToList();
            var servers = picked.Select(d => d.ServerId).Distinct().Count();
            var where = servers > 1 ? " · каждый сервер — в свою папку" : string.Empty;
            var window = CopyWindows.Count > 1 ? $"Окно {_focusedWindow.NumberText} · " : string.Empty;

            return $"{window}Будет скачано отмеченных объектов: {picked.Count} из {Downloads.Count} · с серверов: {servers}{where}.";
        }
    }

    /// <summary>Добавить выбранное в обзоре сервера, без повторов. Вернёт, сколько добавилось.</summary>
    /// <param name="into">В какое окно; null — в окно в фокусе.</param>
    public int AddDownloads(ServerEntry server, IEnumerable<(string Path, bool IsDirectory, long Size, DateTime Modified)> picked, CopyWindow? into = null)
    {
        var added = 0;
        var downloads = (into is not null && CopyWindows.Contains(into) ? into : _focusedWindow).Downloads;

        foreach (var (raw, isDirectory, size, modified) in picked)
        {
            var path = SshService.NormalizeRemote(raw);

            if (path.Length <= 1 || downloads.Any(d => d.ServerId == server.Id && d.Path == path))
            {
                continue;
            }

            downloads.Add(new DownloadItem(server.Id, path, isDirectory, size, modified));
            added++;
        }

        // у новых строк размер, файлы и «со звёздочкой» — сразу, а не с очередной проверкой
        if (added > 0)
        {
            _checkDownloadsAgain = _checkingDownloads;
            _ = RefreshDownloadsAsync();
        }

        return added;
    }

    /// <summary>
    /// Строке скачивания вписали другой путь на том же сервере: она встаёт на то же место заново,
    /// размер и дата — после проверки списка. Вернёт причину отказа или null.
    /// </summary>
    public string? ReplaceDownloadPath(DownloadItem item, string raw) =>
        ReplaceDownload(item, item.ServerId, raw, item.IsDirectory, -1, default);

    /// <summary>То же для адреса из обзора: там могли выбрать и другой сервер, а вид, размер и дата уже известны.</summary>
    public string? ReplaceDownload(DownloadItem item, string serverId, string raw, bool isDirectory, long size, DateTime modified)
    {
        var path = SshService.NormalizeRemote(raw.Trim().Trim('"'));
        var list = CopyWindows.FirstOrDefault(w => w.Downloads.Contains(item))?.Downloads ?? Downloads;
        var index = list.IndexOf(item);

        if (index < 0 || (serverId == item.ServerId && path == item.Path))
        {
            return null;
        }

        if (path.Length <= 1)
        {
            return "корень «/» целиком не скачивается — укажите папку";
        }

        if (list.Any(d => !ReferenceEquals(d, item) && d.ServerId == serverId && d.Path == path))
        {
            return "этот путь уже есть в списке";
        }

        list[index] = new DownloadItem(serverId, path, isDirectory, size, modified) { IsSelected = item.IsSelected };
        _ = RefreshDownloadsAsync();
        return null;
    }

    private bool _checkingDownloads;
    private bool _checkDownloadsAgain;

    /// <summary>
    /// Список скачивания — заново с серверов: размер, число файлов в папке, дата самого свежего
    /// файла, пропавшие пути. Одна команда на сервер; на время скачивания не трогаем.
    /// </summary>
    public async Task RefreshDownloadsAsync()
    {
        if (_checkingDownloads || CopyBatch.IsRunning || !AllDownloads.Any())
        {
            return;
        }

        _checkingDownloads = true;

        try
        {
            var settings = AppState.Current.Settings;

            // у каждого окна свои исключения
            var rows = CopyWindows.SelectMany(w =>
            {
                var filter = ExcludesOf(w);
                return w.Downloads.Select(d => (Item: d, Filter: filter));
            });

            await Task.WhenAll(rows
                .GroupBy(r => r.Item.ServerId)
                .Select(g => (Server: AppState.Current.Servers.FirstOrDefault(s => s.Id == g.Key), Rows: g.ToList()))
                .Where(g => g.Server is not null)
                .Select(g => CheckDownloadsAsync(g.Server!, g.Rows, settings)));

            _ = FollowUnsentAsync();
        }
        finally
        {
            _checkingDownloads = false;

            if (_checkDownloadsAgain)
            {
                _checkDownloadsAgain = false;
                _ = RefreshDownloadsAsync();
            }
        }
    }

    private static async Task CheckDownloadsAsync(ServerEntry server, List<(DownloadItem Item, ExcludeFilter Filter)> rows, AppSettings settings)
    {
        var items = rows.Select(r => r.Item).ToList();

        // сама строка под исключением — внутрь неё не смотрим, как и настоящее скачивание
        var outside = rows.Select(r => !r.Filter.IsEmpty && r.Filter.Excludes(r.Item.Name, r.Item.IsDirectory)).ToList();

        // строка ответа: вид|файлов|байт|дата|номер строки — по номеру, путь может содержать «|».
        // С исключениями ещё и опись папки: маски сверяются здесь, те же, что при скачивании
        var script = new StringBuilder();

        for (var i = 0; i < items.Count; i++)
        {
            var p = SshService.ShellQuote(items[i].Path);
            script.Append("p=").Append(p).Append("; if [ -d \"$p\" ]; then find -H \"$p\" -type f -printf '%s %T@\\n' 2>/dev/null")
                .Append(" | awk 'BEGIN{n=0;s=0;m=0} {n++; s+=$1; if ($2>m) m=$2} END{printf \"D|%d|%.0f|%d|").Append(i).Append("\\n\", n, s, m}'");

            if (!rows[i].Filter.IsEmpty && !outside[i])
            {
                script.Append("; find -H \"$p\" -mindepth 1 \\( -type d -o -type f \\) -printf 'L|").Append(i).Append("|%y|%s|%P\\n' 2>/dev/null");
            }

            script.Append("; elif [ -e \"$p\" ]; then stat -L -c 'F|1|%s|%Y|").Append(i).Append("' \"$p\"")
                .Append("; else echo 'X|0|0|0|").Append(i).Append("'; fi\n");
        }

        var session = RemoteSession.Acquire(server, settings);

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var (output, _) = await session.ExecAsync(script.ToString(), cts.Token);
            var kept = new Dictionary<int, KeptFiles>();
            var answers = new List<(int Index, char Kind, int Files, long Size, DateTime When)>();

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("L|", StringComparison.Ordinal))
                {
                    var l = line.TrimEnd('\r').Split('|', 5);

                    if (l.Length == 5 && int.TryParse(l[1], out var row) && row >= 0 && row < rows.Count && long.TryParse(l[3], out var length))
                    {
                        if (!kept.TryGetValue(row, out var counter))
                        {
                            kept[row] = counter = new KeptFiles();
                        }

                        counter.Add(rows[row].Filter, rows[row].Item.Name, l[2] == "d", length, l[4]);
                    }

                    continue;
                }

                var f = line.Trim().Split('|');

                if (f.Length != 5
                    || !int.TryParse(f[4], out var i) || i < 0 || i >= items.Count
                    || !int.TryParse(f[1], out var files)
                    || !long.TryParse(f[2], out var size)
                    || !long.TryParse(f[3], out var mtime))
                {
                    continue;
                }

                var when = mtime > 0 ? DateTime.UnixEpoch.AddSeconds(mtime).ToLocalTime() : default;
                answers.Add((i, f[0].Length > 0 ? f[0][0] : 'X', files, size, when));
            }

            // опись идёт после строки папки: итог — когда прочитано всё
            foreach (var (i, kind, files, size, when) in answers)
            {
                var (keptBytes, keptFiles) = kind switch
                {
                    'X' => (-1L, -1),
                    _ when outside[i] => (0L, 0),
                    _ when rows[i].Filter.IsEmpty => (size, kind == 'D' ? files : 1),
                    'D' => kept.TryGetValue(i, out var counter) ? (counter.Bytes, counter.Files) : (0L, 0),
                    _ => (size, 1),
                };

                items[i].Update(size, when, kind == 'D' ? files : -1, missing: kind == 'X', keptBytes, keptFiles);
            }
        }
        catch (Exception ex)
        {
            // сервер не ответил — в списке остаются прежние сведения, разбираться тут нечего
            AppLog.Debug($"Проверка списка скачивания не удалась: {ex.Message}", server.Display);
        }
        finally
        {
            session.Release();
        }
    }

    /// <summary>Счёт описи папки с сервера: как при скачивании, под исключённую папку не заглядываем. Родитель в описи всегда раньше детей.</summary>
    private sealed class KeptFiles
    {
        private readonly HashSet<string> _cut = new(StringComparer.Ordinal);

        public long Bytes { get; private set; }

        public int Files { get; private set; }

        /// <param name="relative">Путь от папки строки, без её имени.</param>
        public void Add(ExcludeFilter filter, string root, bool isDirectory, long size, string relative)
        {
            var slash = relative.LastIndexOf('/');
            var inCut = slash >= 0 && _cut.Contains(relative[..slash]);

            if (inCut || filter.Excludes(root + "/" + relative, isDirectory))
            {
                if (isDirectory)
                {
                    _cut.Add(relative);
                }

                return;
            }

            if (!isDirectory)
            {
                Bytes += size;
                Files++;
            }
        }
    }

    /// <summary>Имя подпапки сервера: его имя в списке, без знаков, которых не любит Windows.</summary>
    private static string FolderName(ServerEntry server)
    {
        var name = string.IsNullOrWhiteSpace(server.PaneLabel) ? server.Host : server.PaneLabel;
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return clean.Length == 0 ? server.Id : clean;
    }

    /// <summary>
    /// Скачивание отмеченных строк, каждой со своего сервера. С одного сервера — прямо в папку,
    /// с нескольких — каждый в свою подпапку, иначе одинаковые файлы затёрли бы друг друга.
    /// Умный режим не спрашивает: на сервере ничего не меняется.
    /// </summary>
    public async Task<bool> RunDownloadAsync(bool notify = true)
    {
        if (CopyBatch.IsRunning)
        {
            AppLog.Hint("Скачивание уже идёт.", LogLevel.Warning);
            return false;
        }

        if (DownloadInputs("скачивание", "скачивать", create: true) is not { } jobs)
        {
            return false;
        }

        await DownloadJobsAsync(jobs);

        if (notify)
        {
            AppState.Current.NotifyFinished(CopyBatch);
        }

        return true;
    }

    /// <summary>
    /// Что скачивать, с каких серверов и куда — одни проверки у скачивания и сравнения.
    /// Null — делать нечего, причина уже показана.
    /// </summary>
    /// <param name="create">Создать папку, если её нет: сравнению она не нужна — без неё просто ничего нет здесь.</param>
    private List<DownloadJob>? DownloadInputs(string what, string verb, bool create)
    {
        // окна без отмеченных строк просто пропускаются, как при копировании
        var windows = CopyWindows.Where(w => w.Downloads.Any(d => d.IsSelected)).ToList();

        if (windows.Count == 0)
        {
            Refuse(what, !AllDownloads.Any()
                ? "Список пуст — выберите файлы и папки кнопкой «Выбрать на сервере»."
                : $"Ни одна строка не отмечена — {verb} нечего.");
            return null;
        }

        var gone = PickedDownloads.Where(d => d.Server is null).ToList();

        if (gone.Count > 0)
        {
            AppLog.Warn(OnTab($"Строк, чей сервер удалён из списка, — {gone.Count}: они пропущены"));
        }

        var jobs = new List<DownloadJob>();

        foreach (var window in windows)
        {
            // по серверам; путь внутри другого из списка того же сервера скачался бы дважды
            var plan = window.Downloads
                .Where(d => d.IsSelected && d.Server is not null)
                .GroupBy(d => d.Server!)
                .ToDictionary(
                    g => g.Key,
                    g =>
                    {
                        var paths = g.Select(d => d.Path.TrimEnd('/')).Where(p => p.Length > 0).Distinct().ToList();
                        return paths.Where(p => !paths.Any(q => q != p && p.StartsWith(q + "/", StringComparison.Ordinal))).ToList();
                    });

            if (plan.Count == 0)
            {
                continue;
            }

            var folder = window.LocalFolder.Trim();

            if (folder.Length == 0)
            {
                Refuse(what, (CopyWindows.Count > 1 ? $"Окно {window.LogName}: не" : "Не") + " указана папка здесь, на компьютере.");
                return null;
            }

            jobs.Add(new DownloadJob(window, plan, folder, UploadOptionsOf(window), OptionsOf(window).VerifyChecksums));
        }

        if (jobs.Count == 0)
        {
            Refuse(what, $"Серверы отмеченных строк удалены из списка — {verb} не с чем.");
            return null;
        }

        if (create)
        {
            foreach (var folder in jobs.Select(j => j.Folder).Distinct())
            {
                try
                {
                    Directory.CreateDirectory(folder);
                }
                catch (Exception ex)
                {
                    Refuse(what, $"Папку «{folder}» не создать: {ex.Message}");
                    return null;
                }
            }
        }

        return jobs;
    }

    /// <summary>Что скачивает одно окно. У «Проводника» окна нет — там задание одно и без номера.</summary>
    public sealed record DownloadJob(
        CopyWindow? Window,
        IReadOnlyDictionary<ServerEntry, List<string>> Plan,
        string Folder,
        UploadOptions Options,
        bool Verify);

    /// <summary>
    /// Скачивание — одно на всю программу: его зовут и «Скачивание», и «Проводник». С одного
    /// сервера — прямо в папку, с нескольких — каждый в свою подпапку.
    /// </summary>
    public Task DownloadCoreAsync(
        IReadOnlyDictionary<ServerEntry, List<string>> plan,
        string folder,
        UploadOptions options,
        bool verify,
        ConfirmReplace? confirmReplace = null,
        bool concurrent = false) =>
        DownloadJobsAsync([new DownloadJob(null, plan, folder, options, verify)], confirmReplace, concurrent);

    /// <summary>
    /// Задания идут на каждом сервере по порядку: следующее окно — после успешного предыдущего,
    /// не дожидаясь остальных серверов. Встало на сервере — дальше на нём не идёт.
    /// </summary>
    private async Task DownloadJobsAsync(
        IReadOnlyList<DownloadJob> jobs,
        ConfirmReplace? confirmReplace = null,
        bool concurrent = false)
    {
        var rows = jobs.SelectMany(j => j.Plan.Keys).Distinct()
            .SelectMany(server => jobs.Where(j => j.Plan.ContainsKey(server)).Select(job => (Server: server, Job: job)))
            .ToList();
        var settings = AppState.Current.Settings;
        var gate = new Lock();
        var receivedMax = 0;
        var consideredMax = 0;
        var receivedOn = new Dictionary<ServerEntry, int>();
        var consideredOn = new Dictionary<ServerEntry, int>();

        // для «Нескачанных»: дата строк до скачивания — сменится по ходу, строка останется нескачанной
        var dates = DownloadDates(jobs);

        foreach (var job in jobs)
        {
            AppLog.Started(OnTab((CopyWindows.Count > 1 ? $"Окно {job.Window?.LogName}: скачивание" : "Скачивание")
                + $": путей {job.Plan.Values.Sum(p => p.Count)} в {job.Folder}"), ActionLog.WhereText(job.Plan.Keys));
        }

        SetScope();
        ShowStatus("выполняется", string.Empty, WorkspaceStatus.Running);
        AppState.Current.RequestTerminal();
        ShowResults(CopyBatch);

        await CopyBatch.RunAsync(
            Operation("Скачивание"),
            rows.Select(r => r.Server).ToList(),
            async (server, result, ct) =>
            {
                var job = (DownloadJob)result.Job!;

                result.Message = "скачивание…";

                var root = job.Plan.Count > 1 ? Path.Combine(job.Folder, FolderName(server)) : job.Folder;
                result.Append(BlockParams);
                result.Append("куда: " + root);
                result.Append($"путей: {job.Plan[server].Count}" + (job.Verify ? ", с проверкой копий" : string.Empty));
                result.Append("── скачивание ──");

                var stats = await DownloadService.DownloadAsync(
                    server, settings, job.Plan[server], root, job.Options, job.Verify, line => result.Append(line), ct, result.SetTransfer, confirmReplace);

                result.Append(
                    $"итог: получено {stats.Received} ({TransferItem.Format(stats.Bytes)}) · такие же {stats.UpToDate}"
                    + (stats.Skipped > 0 ? $" · уже были, не тронуты {stats.Skipped}" : string.Empty)
                    + (stats.Dirs > 0 ? $" · папок создано {stats.Dirs}" : string.Empty));

                // на ярлыке — итог самого загруженного сервера по всем его окнам
                lock (gate)
                {
                    receivedOn[server] = receivedOn.GetValueOrDefault(server) + stats.Received;
                    consideredOn[server] = consideredOn.GetValueOrDefault(server) + stats.Received + stats.UpToDate + stats.Skipped;
                    receivedMax = Math.Max(receivedMax, receivedOn[server]);
                    consideredMax = Math.Max(consideredMax, consideredOn[server]);
                }

                if (stats.Mismatches > 0)
                {
                    result.Message = $"расхождений сумм: {stats.Mismatches}";
                    result.Status = OperationStatus.Failed;
                    FlashFailure(job.Window);
                    return;
                }

                if (stats.Failed > 0)
                {
                    result.Message = $"не скачано {stats.Failed}, получено {stats.Received}";
                    result.Status = OperationStatus.Failed;
                    return;
                }

                result.Message = $"получено {stats.Received}, {TransferItem.Format(stats.Bytes)}"
                    + (stats.UpToDate > 0 ? $" · такие же {stats.UpToDate}" : string.Empty)
                    + (stats.Skipped > 0 ? $" · уже были, не тронуты {stats.Skipped}" : string.Empty);
                result.Status = OperationStatus.Success;

                if (stats.Skipped == 0)
                {
                    RememberReceived(dates, job, server);
                }
            },
            settings.MaxParallel,
            concurrent: concurrent,
            prepare: WindowRows(rows, j => j.Window, null, chained: true));

        if (dates.Count > 0)
        {
            Save();
            _ = FollowUnsentAsync();
        }

        // запуски «Проводника» идут рядом — итог на ярлыке, когда закончился последний
        if (CopyBatch.IsRunning)
        {
            return;
        }

        FinishStatus(
            CopyBatch,
            "скачано",
            $"{receivedMax} из {consideredMax}",
            receivedMax == 0 && CopyBatch.RunFailed == 0
                ? "скачивать было нечего: здесь файлы такие же"
                : null);
    }

    // ── связка «копирование → личные команды» ──────────────────────────────────

    private string _afterCopyTabId = string.Empty;
    private bool _afterCopyEnabled;

    /// <summary>Личная командная вкладка этой вкладки копирования; пусто — её нет.</summary>
    public string AfterCopyTabId => _afterCopyTabId;

    public Workspace? Personal => _afterCopyTabId.Length == 0
        ? null
        : AppState.Current.CommandTabs.FirstOrDefault(t => t.Id == _afterCopyTabId);

    public bool HasPersonal => Personal is not null;

    /// <summary>Свитч на ярлыке: после удачного копирования выполнить личные команды.</summary>
    public bool AfterCopyEnabled
    {
        get => _afterCopyEnabled;
        set
        {
            if (Set(ref _afterCopyEnabled, value))
            {
                RaiseChain();
                Save();
            }
        }
    }

    /// <summary>Это вкладка раздела «Копирование» — у неё на ярлыке свитч личных команд.</summary>
    public bool IsCopyTab => AppState.Current.Workspaces.Contains(this);

    /// <summary>Память вкладки проводника: папки обеих панелей, курсор, сортировка. Правит её страница проводника.</summary>
    public ExplorerTabState Explorer { get; set; } = new();

    /// <summary>Сервер вкладки проводника — отмеченный в панели слева (в проводнике там всегда один).</summary>
    public ServerEntry? ExplorerServer => CopyTargets.FirstOrDefault();

    /// <summary>Открытая папка сервера сменилась — ярлык показывает её.</summary>
    public void RaiseExplorerPath() => RaiseTabCounts();

    public void LinkPersonal(Workspace personal)
    {
        _afterCopyTabId = personal.Id;
        RaiseChain();
        Save();
    }

    /// <summary>Личную вкладку закрыли: свитч гаснет, следующее включение создаст новую.</summary>
    /// <summary>Закрытую вкладку вернули: связь с личной вкладкой — какой была.</summary>
    public void LinkPersonal(string tabId, bool enabled)
    {
        _afterCopyTabId = tabId;
        _afterCopyEnabled = enabled;
        RaiseChain();
        Save();
    }

    public void UnlinkPersonal()
    {
        _afterCopyTabId = string.Empty;
        _afterCopyEnabled = false;
        RaiseChain();
        Save();
    }

    public void RaiseChain()
    {
        RaiseMany(
            nameof(AfterCopyEnabled), nameof(Personal), nameof(HasPersonal), nameof(IsCopyTab),
            nameof(Notices), nameof(HasNotices), nameof(PersonalButtonText));

        // галочка «+ команды» в терминале видна, только пока свитч включён. Current ещё нет,
        // пока вкладки читаются при запуске — тогда и показывать нечего
        AppState.Current?.RaiseChainCopy();
    }

    /// <summary>
    /// Плашка у пути на сервере: что мешает сделать то, о чём попросили, и требует внимания,
    /// пока не устранено. Не всплывающие подсказки — висит, пока причина есть.
    /// Сейчас — про личные команды после копирования, если их свитч включён.
    /// </summary>
    public IReadOnlyList<string> Notices
    {
        get
        {
            if (!_afterCopyEnabled || Personal is not { } personal)
            {
                return [];
            }

            return personal.RunnableSteps(null, onlyThis: false).Count == 0
                ? ["Вкладка команд пустая — после копирования выполнять нечего."]
                : [];
        }
    }

    public bool HasNotices => Notices.Count > 0;

    /// <summary>Кнопка в мини-терминале: ведёт к личной вкладке, а её нет — создаёт.</summary>
    public string PersonalButtonText => Personal is null ? "Создать команды после копирования" : "Команды после копирования";

    /// <summary>Плашку пересчитать: у вкладки копирования — свою, у личной — её владельца.</summary>
    private void RaiseNotices()
    {
        RaiseMany(nameof(Notices), nameof(HasNotices));

        if (IsPersonal)
        {
            Owner?.RaiseNotices();
        }
    }

    /// <summary>Текст первого окна — для всего, что знает только про одну команду.</summary>
    public string CommandText
    {
        get => Steps[0].Text;
        set => Steps[0].Text = value;
    }

    public bool UseSudo
    {
        get => Steps[0].UseSudo;
        set => Steps[0].UseSudo = value;
    }

    /// <summary>Новое пустое окно сразу под <paramref name="after"/> (или в конце).</summary>
    public CommandStep AddStep(CommandStep? after)
    {
        var step = new CommandStep();
        var index = after is null ? Steps.Count : Steps.IndexOf(after) + 1;
        Steps.Insert(Math.Clamp(index, 0, Steps.Count), step);
        return step;
    }

    /// <summary>Убрать окно. Последнее оставшееся не убирается.</summary>
    public bool RemoveStep(CommandStep step) => Steps.Count > 1 && Steps.Remove(step);

    /// <summary>Курсор встал в окно команды: рамка у него, и сюда подставляются история и избранное.</summary>
    public void FocusStep(CommandStep step)
    {
        foreach (var each in Steps)
        {
            each.IsFocused = ReferenceEquals(each, step);
        }
    }

    /// <summary>Заменить все окна набором — из избранного или из файла.</summary>
    public void SetSteps(IEnumerable<SavedCommandStep> saved)
    {
        Steps.Clear();

        foreach (var s in saved)
        {
            Steps.Add(CommandStep.From(s));
        }

        if (Steps.Count == 0)
        {
            Steps.Add(new CommandStep());
        }
    }

    public List<SavedCommandStep> SaveSteps() => Steps.Select(s => s.ToSaved()).ToList();

    /// <summary>Все окна одним текстом — чтобы узнать «ту же команду ещё раз».</summary>
    public string StepsSignature => string.Join("\n\u241E\n", Steps.Select(s => s.Text.Trim()));

    /// <summary>Во всех окнах пусто — «Выполнить тут» без дела.</summary>
    public bool AllStepsEmpty => Steps.All(s => s.IsEmpty);

    private void OnStepsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var step in e.OldItems?.OfType<CommandStep>() ?? [])
        {
            step.Changed -= OnStepChanged;
        }

        foreach (var step in e.NewItems?.OfType<CommandStep>() ?? [])
        {
            step.Changed += OnStepChanged;
        }

        // Clear не присылает старые элементы — снимаем подписку со всех и вешаем заново
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var step in Steps)
            {
                step.Changed -= OnStepChanged;
                step.Changed += OnStepChanged;
            }
        }

        for (var i = 0; i < Steps.Count; i++)
        {
            Steps[i].Place(i + 1, Steps.Count);
        }

        if (Steps.Count > 0)
        {
            // окно в фокусе убрали или заменили всё сразу — фокус у первого
            if (!Steps.Any(s => s.IsFocused))
            {
                Steps[0].IsFocused = true;
            }

            OnStepChanged();
        }
    }

    private void OnStepChanged()
    {
        // «пусто отсюда и ниже» — снизу вверх
        var tail = true;

        for (var i = Steps.Count - 1; i >= 0; i--)
        {
            tail &= Steps[i].IsEmpty;
            Steps[i].IsTailEmpty = tail;
        }

        RaiseMany(nameof(CommandText), nameof(UseSudo), nameof(CommandSizeInfo), nameof(TabCounts), nameof(TabTooltip), nameof(AllStepsEmpty));
        RaiseNotices();

        if (!_restoring)
        {
            Save();
        }
    }

    private static IReadOnlyList<ServerEntry> Picked(IEnumerable<ServerCheck> set) => set
        .Where(c => c.IsSelected && !string.IsNullOrWhiteSpace(c.Server.Host))
        .Select(c => c.Server)
        .ToList();

    /// <summary>Отмеченные серверы того набора, который открыт сейчас.</summary>
    public IReadOnlyList<ServerEntry> SelectedServers => Picked(Servers);

    /// <summary>Куда пойдёт копирование этой вкладки — отмеченные из выбранного набора.</summary>
    public IReadOnlyList<ServerEntry> CopyTargets => Picked(InActiveSet(CopyServers));

    /// <summary>Где выполнится команда этой вкладки — отмеченные из выбранного набора.</summary>
    public IReadOnlyList<ServerEntry> CommandTargets => Picked(InActiveSet(CommandServers));

    /// <summary>Какой набор галочек сейчас правится — чтобы «отмечено 0 из 6» не путало разделы.</summary>
    private bool CommandSet => AppState.Current.Section == AppSection.Command;

    /// <summary>
    /// Заголовок панели слева — ровно эта фраза, без раздела и без счётчика отмеченных:
    /// место узкое, прежнее «СЕРВЕРЫ ДЛЯ КОПИРОВАНИЯ · ОТМЕЧЕНО 0 ИЗ 2» обрезалось
    /// на полуслове. Счёт отмеченных остался в <see cref="SelectionInfo"/> на страницах.
    /// </summary>
    public string PaneTitle => "Целевые сервера";

    public string SelectionInfo =>
        AppState.Current.Section switch
        {
            AppSection.Command => "для команды",
            AppSection.Download => "для скачивания",
            AppSection.Explorer => "для проводника",
            _ => "для копирования",
        }
        + $" · всего: {Servers.Count} · отмечено: {SelectedServers.Count}";

    public Visibility PaneEmptyHintVisibility =>
        Servers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public string TargetsInfo
    {
        get
        {
            var servers = CopyTargets.Count;

            if (servers == 0)
            {
                return "Ни один сервер не отмечен — отметьте нужные в панели слева "
                    + "или на вкладке «Серверы» (галочки копирования и команды разные).";
            }

            var picked = Items.Count(i => i.IsSelected);
            var window = CopyWindows.Count > 1 ? $"Окно {_focusedWindow.NumberText} · " : string.Empty;
            return $"{window}Будет скопировано отмеченных объектов: {picked} из {Items.Count} · на серверов: {servers}.";
        }
    }

    public string CommandTargetsInfo
    {
        get
        {
            var count = CommandTargets.Count;
            return count == 0
                ? "Ни один сервер не отмечен — отметьте нужные в панели слева: "
                    + "здесь свой набор, отдельный от копирования."
                : $"Будет выполнено на {count} сервере(ах).";
        }
    }

    /// <summary>Подсказка под полем ввода: длина команды ничем не ограничена, но её полезно видеть.</summary>
    public string CommandSizeInfo
    {
        get
        {
            var filled = Steps.Where(s => s.Text.Length > 0).ToList();

            if (filled.Count == 0)
            {
                return string.Empty;
            }

            var chars = filled.Sum(s => s.Text.Length);
            var lines = filled.Sum(s => s.Text.Split('\n').Length);
            var windows = Steps.Count > 1 ? $"окон: {Steps.Count} · " : string.Empty;
            return $"{windows}символов: {chars} · строк: {lines}";
        }
    }

    /// <summary>Состояние галочки в шапке таблицы файлов: все / ничего / частично.</summary>
    public bool? AllItemsSelected => TriState(Items.Count, Items.Count(i => i.IsSelected));

    /// <summary>То же для таблицы серверов.</summary>
    public bool? AllServersSelected => TriState(Servers.Count, Servers.Count(s => s.IsSelected));

    /// <summary>
    /// Ярлык вкладки фиксированного размера: пока висит статус, вместо названия показываются
    /// две короткие строки — счётчик и что это было. Ни ширина, ни высота не меняются.
    /// </summary>
    public string StatusBadge
    {
        get => _statusBadge;
        private set => Set(ref _statusBadge, value);
    }

    public string StatusCaption
    {
        get => _statusCaption;
        private set { if (Set(ref _statusCaption, value)) { Raise(nameof(StatusCaptionVisibility)); } }
    }

    /// <summary>Вторая строка ярлыка нужна только под ошибки: без них она не занимает места.</summary>
    public Visibility StatusCaptionVisibility =>
        string.IsNullOrEmpty(StatusCaption) ? Visibility.Collapsed : Visibility.Visible;

    public string StatusTooltip
    {
        get => _statusTooltip;
        private set { if (Set(ref _statusTooltip, value)) { Raise(nameof(TabTooltip)); } }
    }

    /// <summary>
    /// Вторая строка ярлыка. Пишет то, что относится к открытому разделу:
    /// на копировании — отмеченные объекты, на команде — её размер, серверы — всегда.
    /// </summary>
    public string TabCounts
    {
        get
        {
            var servers = $"серверов {SelectedServers.Count}";

            return AppState.Current.Section switch
            {
                // «файлов» здесь врало: в списке и папки тоже, а считаются они поштучно,
                // а не по содержимому — поэтому нейтральное «отмечено»
                AppSection.Files =>
                    $"Отмечено: {AllItems.Count(i => i.IsSelected)} · Серверов: {SelectedServers.Count}",
                AppSection.Download =>
                    $"Отмечено: {PickedDownloads.Count()} · Серверов: {PickedDownloads.Select(d => d.ServerId).Distinct().Count()}",
                AppSection.Explorer => ExplorerServer?.PaneLabel ?? "сервер не выбран",
                AppSection.Command when Steps.Count > 1 => $"{servers} · окон {Steps.Count}",
                AppSection.Command => string.IsNullOrWhiteSpace(CommandText)
                    ? $"{servers} · нет команды"
                    : $"{servers} · {CommandText.Trim().Length} симв.",
                _ => $"{servers} из {Servers.Count}",
            };
        }
    }

    public void RaiseTabCounts() => RaiseMany(nameof(TabCounts), nameof(TabDetail), nameof(HasTabDetail), nameof(TabTooltip));

    /// <summary>
    /// Вторая строка под названием — у проводника открытая на сервере папка, коротко:
    /// «…/папка». Полный путь — в подсказке ярлыка и в самой панели.
    /// </summary>
    public string TabDetail
    {
        get
        {
            if (AppState.Current.Section != AppSection.Explorer || ExplorerServer is null)
            {
                return string.Empty;
            }

            var path = Explorer.Remote.Path.TrimEnd('/');

            if (Explorer.Remote.Path.Length == 0)
            {
                return "~";
            }

            if (path.Length == 0)
            {
                return "/";
            }

            var last = path.LastIndexOf('/');
            return last <= 0 ? path : "…/" + path[(last + 1)..];
        }
    }

    public bool HasTabDetail => TabDetail.Length > 0;

    /// <summary>
    /// Открыли другой раздел — панель серверов должна показать его набор галочек.
    /// Сама коллекция другая, поэтому список перепривязывается целиком.
    /// </summary>
    public void RaiseSection()
    {
        Raise(nameof(Servers));
        RaiseMany(nameof(IsTabDimmed), nameof(TabOpacity), nameof(IsTabPickable));
        RaiseSelectionInfo();
    }

    /// <summary>Подсказка на ярлыке: пока висит статус — он, в остальное время — что можно сделать.</summary>
    public string TabTooltip => HasStatus
        ? $"{Title} · {StatusTooltip}"
        : IsPersonal
            ? $"{Title} — команды после копирования этой вкладки. {TabCounts}"
        : IsMain
            ? $"{Title} — {TabSummary}. Первая вкладка: её нельзя закрыть, переставить и переименовать"
            : $"{Title} — {TabSummary}. Перетащите мышью, чтобы поменять вкладки местами";

    /// <summary>Строки ярлыка целиком — у проводника с полным путём, который на ярлык не влез.</summary>
    private string TabSummary => HasTabDetail
        ? $"{TabCounts} · {(Explorer.Remote.Path.Length > 0 ? Explorer.Remote.Path : "~")}"
        : TabCounts;

    /// <summary>
    /// Статус на ярлыке цветом не выделяется — только жирным шрифтом. Само значение
    /// нужно, чтобы знать, есть ли что показывать.
    /// </summary>
    public WorkspaceStatus Status
    {
        get => _status;
        private set { if (Set(ref _status, value)) { RaiseMany(nameof(HasStatus), nameof(ShowsTabStatus), nameof(HidesTabCounts)); } }
    }

    public bool HasStatus => Status != WorkspaceStatus.None;

    /// <summary>
    /// Статус виден на ярлыке вместо строк под названием. У проводника — только пока идёт
    /// перенос: потом на месте снова сервер и папка, по ним вкладку и узнают.
    /// </summary>
    public bool ShowsTabStatus => HasStatus && (Status == WorkspaceStatus.Running || !IsExplorerTab);

    /// <summary>У проводника на время переноса статус встаёт на место строк «сервер / папка».</summary>
    public bool HidesTabCounts => Status == WorkspaceStatus.Running && IsExplorerTab;

    private bool IsExplorerTab => AppState.Current.ExplorerTabs.Contains(this);

    public bool IsBusy => CopyBatch.IsRunning || CommandBatch.IsRunning || CheckBatch.IsRunning;

    /// <summary>Открытая сейчас вкладка — её кирпич на доске вкладок подсвечен акцентом.</summary>
    public bool IsActive => ReferenceEquals(this, AppState.Current.ActiveWorkspace);

    /// <summary>
    /// Потушенных вкладок больше нет: у «Команды» свои вкладки, и каждая рабочая.
    /// Свойство оставлено для разметки ярлыков.
    /// </summary>
    public bool IsTabDimmed => false;

    public double TabOpacity => IsTabDimmed ? 0.4 : 1.0;

    public bool IsTabPickable => !IsTabDimmed;

    public void RaiseDim() => Raise(nameof(IsActive));

    // ── серверы ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Подгоняет наборы галочек под общий список серверов, сохраняя уже расставленные отметки.
    /// Наборов два — копирование и команда, оба живут по одному списку серверов.
    /// Новый сервер приходит неотмеченным везде: отметка — только руками, иначе на него
    /// начинают копировать и выполнять команды, хотя его никто не выбирал.
    /// </summary>
    public void SyncServers(IList<ServerEntry> all)
    {
        Sync(CopyServers, all);
        Sync(CommandServers, all);
        SyncLock();

        // панель получает отфильтрованный список, а не саму коллекцию — перечитывает его по сигналу
        Raise(nameof(Servers));
        RaiseSelectionInfo();
    }

    private void Sync(ObservableCollection<ServerCheck> set, IList<ServerEntry> all)
    {
        for (var i = set.Count - 1; i >= 0; i--)
        {
            if (!all.Contains(set[i].Server))
            {
                set[i].PropertyChanged -= OnServerCheckChanged;
                set.RemoveAt(i);
            }
        }

        for (var i = 0; i < all.Count; i++)
        {
            var existing = set.FirstOrDefault(c => ReferenceEquals(c.Server, all[i]));

            if (existing is null)
            {
                var check = new ServerCheck(all[i], isSelected: false);
                check.PropertyChanged += OnServerCheckChanged;
                set.Insert(Math.Min(i, set.Count), check);
                continue;
            }

            var index = set.IndexOf(existing);

            if (index != i && i < set.Count)
            {
                set.Move(index, i);
            }
        }
    }

    /// <summary>Отметки серверов для копирования.</summary>
    public void SelectServers(IEnumerable<string> ids) => Select(CopyServers, ids);

    /// <summary>Отметки серверов для команды — набор отдельный.</summary>
    public void SelectCommandServers(IEnumerable<string> ids) => Select(CommandServers, ids);

    private void Select(ObservableCollection<ServerCheck> set, IEnumerable<string> ids)
    {
        var picked = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);

        foreach (var check in set)
        {
            check.IsSelected = picked.Contains(check.Server.Id);
        }

        RaiseSelectionInfo();
    }

    /// <summary>
    /// Галочка «Отметить все» над серверами панели: отмечены все — стоит, ни одного — пусто,
    /// часть — черта. Щелчок работает как <see cref="ToggleAllServers"/>: при части отмечает все,
    /// а не снимает, — какое значение пришло от галочки, неважно.
    /// </summary>
    public bool? PaneAllChecked
    {
        get
        {
            var set = Servers;
            var picked = set.Count(s => s.IsSelected);

            return picked == 0 ? false : picked == set.Count ? true : null;
        }
        set => ToggleAllServers();
    }

    /// <summary>Отметить или снять все — в том наборе, который открыт сейчас.</summary>
    public void ToggleAllServers()
    {
        if (BlockedByLock())
        {
            return;
        }

        var set = Servers;
        var selectAll = set.Any(s => !s.IsSelected);

        foreach (var check in set)
        {
            check.IsSelected = selectAll;
        }

        RaiseSelectionInfo();
        AppLog.Hint(selectAll ? $"Отмечены все целевые сервера: {set.Count}." : "Отметки целевых серверов сняты.");
    }

    // ── список копирования ─────────────────────────────────────────────────────

    /// <param name="into">В какое окно; null — в окно в фокусе.</param>
    public int AddPaths(IEnumerable<string> paths, CopyWindow? into = null)
    {
        var added = 0;
        var window = into is not null && CopyWindows.Contains(into) ? into : _focusedWindow;
        var items = window.Items;

        foreach (var path in paths)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                continue;
            }

            if (items.Any(i => string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            items.Add(new TransferItem(path, ExcludesOf(window)));
            added++;
        }

        // правка списка ничего не делает с файлами — в журнал не идёт, что ушло, видно в запуске
        return added;
    }

    /// <summary>
    /// Строке вписали другой путь: она встаёт на то же место заново, с прежней галочкой.
    /// Вернёт причину отказа или null, если путь сменился.
    /// </summary>
    public string? ReplaceItemPath(TransferItem item, string raw)
    {
        var path = raw.Trim().Trim('"');
        var window = CopyWindows.FirstOrDefault(w => w.Items.Contains(item)) ?? _focusedWindow;
        var items = window.Items;
        var index = items.IndexOf(item);

        if (index < 0 || string.Equals(path, item.FullPath, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return "такого файла или папки на компьютере нет";
        }

        if (items.Any(i => !ReferenceEquals(i, item) && string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase)))
        {
            return "этот путь уже есть в списке";
        }

        items[index] = new TransferItem(path, ExcludesOf(window)) { IsSelected = item.IsSelected };
        return null;
    }

    public void RemoveItems(IEnumerable<TransferItem> items)
    {
        foreach (var item in items.ToList())
        {
            CopyWindows.FirstOrDefault(w => w.Items.Contains(item))?.Items.Remove(item);
        }
    }

    /// <summary>Галочка в общей шапке таблицы: строки всех окон.</summary>
    public bool? AllWindowsItemsSelected => TriState(AllItems.Count(), AllItems.Count(i => i.IsSelected));

    public void ToggleAllWindowsItems()
    {
        var selectAll = AllItems.Any(i => !i.IsSelected);

        foreach (var item in AllItems)
        {
            item.IsSelected = selectAll;
        }

        RaiseCopyInfo();
    }

    public void ToggleAllItems()
    {
        var selectAll = Items.Any(i => !i.IsSelected);

        foreach (var item in Items)
        {
            item.IsSelected = selectAll;
        }

        RaiseCopyInfo();
    }

    /// <summary>
    /// Перечитывает размер и дату изменения всех строк. Обход больших папок долгий,
    /// поэтому работа уходит в фоновый поток, а строки обновляются по мере готовности.
    /// </summary>
    public async Task RefreshItemsInfoAsync()
    {
        if (_refreshingInfo || !AllItems.Any())
        {
            return;
        }

        _refreshingInfo = true;

        try
        {
            // у каждого окна свои исключения; читаются здесь, в потоке окна
            var rows = CopyWindows.SelectMany(w =>
            {
                var filter = ExcludesOf(w);
                return w.Items.Select(i => (Item: i, Filter: filter));
            }).ToList();

            await Task.Run(() =>
            {
                foreach (var (item, filter) in rows)
                {
                    item.Refresh(filter);
                }
            });

            DropMissing(rows.Select(r => r.Item).ToList());
            _ = FollowUnsentAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error(OnTab("Не удалось обновить сведения о файлах: " + ex.Message));
        }
        finally
        {
            _refreshingInfo = false;

            if (_refreshInfoAgain)
            {
                _refreshInfoAgain = false;
                _ = RefreshItemsInfoAsync();
            }
        }
    }

    // ── свитч «Незалитые» ──────────────────────────────────────────────────────

    private bool _pickingUnsent;
    private bool _followingUnsent;
    private bool _followUnsentAgain;
    private int _unsentPresses;

    private IEnumerable<IRowCheck> TickRows => AllItems.Cast<IRowCheck>().Concat(AllDownloads);

    /// <summary>Свитч включён: прежние галочки помнятся. Меняется вместе с <see cref="UnsentOn"/> — отдельно не объявляется.</summary>
    public bool UnsentHeld => TickRows.Any(r => r.Memory.Before is not null);

    /// <summary>Свитч включён, а руками отмечено уже залитое. Меняется вместе с <see cref="UnsentOn"/>.</summary>
    public bool UnsentSentTicked => TickRows.Any(r => r.Memory.Before is not null && r.IsSelected && !r.Memory.Marked);

    /// <summary>Свитч зелёный: галочки стоят так, как их поставил свитч. Тронули руками — краснеет, прежние галочки при этом помнятся.</summary>
    public bool UnsentOn
    {
        get
        {
            var held = TickRows.Where(r => r.Memory.Before is not null).ToList();
            return held.Count > 0 && held.All(r => r.IsSelected == r.Memory.Marked);
        }
    }

    /// <summary>
    /// Включение — галочки на том, что этой программой ещё не копировали или что изменилось после копирования.
    /// Выключение зелёного — галочки, какие были до включения. Красный (галочки правили руками) щелчок
    /// не выключает, а снова ставит галочки на незалитое.
    /// По всем окнам вкладки: «Выполнить» тоже идёт по всем. Даты только здешние, на сервер ничего не уходит.
    /// Пока свитч зелёный, галочки держит <see cref="FollowUnsentAsync"/>.
    /// </summary>
    public async Task ToggleUnsentAsync()
    {
        if (_pickingUnsent)
        {
            return;
        }

        _pickingUnsent = true;
        _unsentPresses++;

        try
        {
            if (UnsentOn)
            {
                foreach (var row in TickRows.Where(r => r.Memory.Before is not null).ToList())
                {
                    row.IsSelected = row.Memory.Before!.Value;
                    row.Memory.Before = null;
                    row.Memory.Marked = false;
                }
            }
            else
            {
                // незалитых нет — кнопка всё равно включается: что отмечать нечего, видно по пустым галочкам
                var (rows, unsent) = await FindUnsentAsync();

                MarkUnsent(rows, unsent);
            }

            Raise(nameof(UnsentOn));
            Save();
        }
        finally
        {
            _pickingUnsent = false;
        }
    }

    /// <summary>
    /// После «Сравнить»: свитч включён — галочки снова ровно на незалитом, уже по свежей сверке, даже если
    /// их потом правили руками и свитч покраснел. Прежние галочки для выключения те же. Выключен — не трогаем.
    /// </summary>
    private async Task RemarkUnsentAsync()
    {
        if (_pickingUnsent || !UnsentHeld)
        {
            return;
        }

        _pickingUnsent = true;
        _unsentPresses++;

        try
        {
            var (rows, unsent) = await FindUnsentAsync();

            MarkUnsent(rows, unsent);
            Raise(nameof(UnsentOn));
            Save();
        }
        finally
        {
            _pickingUnsent = false;
        }
    }

    /// <summary>
    /// Кнопка включена — отмечено ровно незалитое: удачно отправленное теряет галочку, изменившийся файл её получает.
    /// Зовут те, кто узнал новое: конец захода, правка файла, проверка списка. Галочки тронули руками — рамка погасла, не вмешиваемся.
    /// </summary>
    private async Task FollowUnsentAsync()
    {
        if (_followingUnsent)
        {
            _followUnsentAgain = true;
            return;
        }

        if (_pickingUnsent || CopyBatch.IsRunning || !UnsentOn)
        {
            return;
        }

        _followingUnsent = true;

        var presses = _unsentPresses;

        try
        {
            var (rows, unsent) = await FindUnsentAsync();

            // пока читали, могли нажать кнопку, тронуть галочки или начать копирование
            if (presses == _unsentPresses && !CopyBatch.IsRunning && UnsentOn && rows.Any(r => r.IsSelected != unsent.Contains(r)))
            {
                MarkUnsent(rows, unsent);
                Save();
            }
        }
        finally
        {
            _followingUnsent = false;

            if (_followUnsentAgain)
            {
                _followUnsentAgain = false;
                _ = FollowUnsentAsync();
            }
        }
    }

    /// <summary>Дата строк до захода: читается один раз, когда первый сервер начал работу. У «Проводника» окна нет — и строк для памяти нет.</summary>
    private static Dictionary<UploadJob, Lazy<List<string?>>> RowDates(IReadOnlyList<UploadJob> jobs) =>
        jobs.ToDictionary(j => j, j => new Lazy<List<string?>>(() =>
        {
            var filter = j.Options.Excludes();
            return j.Window is null ? [] : j.Payload.Select(i => i.ChangeDate(filter)).ToList();
        }));

    /// <summary>
    /// Строки, о которых известно, что они на всех серверах: залиты, какими были до захода.
    /// Копирование отвечает за окно целиком, сравнение — за каждую строку.
    /// </summary>
    /// <param name="targets">Серверы захода: строка запоминает те, куда шло её окно, — на них её файлы проверены.</param>
    private void RememberSent(
        IReadOnlyList<UploadJob> jobs,
        Dictionary<UploadJob, Lazy<List<string?>>> dates,
        Func<UploadJob, TransferItem, bool> sent,
        IReadOnlyList<ServerEntry> targets)
    {
        foreach (var job in jobs.Where(j => j.Window is not null))
        {
            for (var i = 0; i < job.Payload.Count; i++)
            {
                if (sent(job, job.Payload[i]) && dates[job].Value[i] is { } date)
                {
                    job.Payload[i].Memory.Date = date;
                    job.Payload[i].Memory.Servers = targets.Where(s => Goes(job, s)).Select(s => s.Id).ToList();
                }
            }
        }

        if (jobs.Any(j => j.Window is not null))
        {
            Save();
            _ = FollowUnsentAsync();
        }
    }

    /// <summary>
    /// Для свитча «Отметить незалитые»: файл строки не новее её даты при прошлой удачной заливке на этот сервер —
    /// там он уже проверен. Строкам без такой памяти (сервер новый, память старой версии) не доверяем.
    /// </summary>
    private static Func<string, bool>? SettledOn(UploadJob job, ServerEntry server)
    {
        var roots = new List<(string Root, DateTime Date)>();

        foreach (var item in job.Payload)
        {
            if (item.Memory.Servers?.Contains(server.Id) == true
                && DateTime.TryParse(item.Memory.Date, null, System.Globalization.DateTimeStyles.RoundtripKind, out var date))
            {
                roots.Add((item.FullPath.TrimEnd(Path.DirectorySeparatorChar), date.ToUniversalTime()));
            }
        }

        if (roots.Count == 0)
        {
            return null;
        }

        return path =>
        {
            foreach (var (root, date) in roots)
            {
                if (path.Equals(root, StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return File.GetLastWriteTimeUtc(path) <= date;
                }
            }

            return false;
        };
    }

    /// <summary>Дата строк скачивания до захода — сменится по ходу, строка останется нескачанной.</summary>
    private static List<(DownloadJob Job, DownloadItem Row, string? Date)> DownloadDates(IReadOnlyList<DownloadJob> jobs) =>
        jobs.Where(j => j.Window is not null)
            .SelectMany(j => j.Window!.Downloads.Where(d => d.IsSelected).Select(d => (Job: j, Row: d, Date: d.ChangeDate)))
            .ToList();

    /// <summary>
    /// Строки этого окна с этого сервера получены: у каждой строки сервер один.
    /// После сравнения — только те, в которых ничего не разошлось; разошедшиеся становятся нескачанными.
    /// </summary>
    private static void RememberReceived(
        List<(DownloadJob Job, DownloadItem Row, string? Date)> dates,
        DownloadJob job,
        ServerEntry server,
        CompareStats? compared = null)
    {
        foreach (var (_, row, date) in dates.Where(d => ReferenceEquals(d.Job, job) && d.Row.ServerId == server.Id))
        {
            if (compared is not null && Touched(compared, row.Name))
            {
                row.Memory.Date = null;
            }
            else if (compared is null || job.Options.CompareConfirms)
            {
                row.Memory.Date = date ?? row.Memory.Date;
            }
        }
    }

    /// <summary>Отличие сравнения лежит в этой строке: она сама или что-то внутри неё.</summary>
    private static bool Touched(CompareStats stats, string top) =>
        stats.MissingFiles.Concat(stats.MissingDirs).Concat(stats.Different.Select(d => d.Path))
            .Any(p => p == top || p.StartsWith(top + "/", StringComparison.Ordinal));

    /// <summary>Незалитое: дата есть и не та, что запомнена при последнем удачном заходе.</summary>
    private async Task<(List<IRowCheck> Rows, HashSet<IRowCheck> Unsent)> FindUnsentAsync()
    {
        // у каждого окна свои исключения; читаются здесь, в потоке окна
        var items = CopyWindows.SelectMany(w =>
        {
            var filter = ExcludesOf(w);
            return w.Items.Select(i => (Item: i, Filter: filter));
        }).ToList();

        var itemDates = await Task.Run(() => items.Select(r => r.Item.ChangeDate(r.Filter)).ToList());
        var dated = items.Select((r, i) => ((IRowCheck)r.Item, itemDates[i]))
            .Concat(AllDownloads.Select(d => ((IRowCheck)d, d.Server is null ? null : d.ChangeDate)))
            .ToList();

        return (
            dated.Select(r => r.Item1).ToList(),
            dated.Where(r => r.Item2 is not null && r.Item2 != r.Item1.Memory.Date).Select(r => r.Item1).ToHashSet());
    }

    private static void MarkUnsent(List<IRowCheck> rows, HashSet<IRowCheck> unsent)
    {
        // прежние галочки остаются те, что были до самого первого нажатия
        foreach (var row in rows)
        {
            row.Memory.Before ??= row.IsSelected;
            row.Memory.Marked = unsent.Contains(row);
            row.IsSelected = row.Memory.Marked;
        }
    }

    // ── операции ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Операция не начата. Причину видно сразу плашкой справа внизу (она же запись в журнале)
    /// и меткой на ярлыке вкладки. Журнал сам не открывается: из-за забытой команды человеку
    /// пришлось бы потом возвращаться в терминал.
    /// </summary>
    private bool Refuse(string what, string reason)
    {
        AppLog.Warn(OnTab(reason));
        ShowStatus("не начато", string.Empty, WorkspaceStatus.Warning, $"{what} не начато: {reason}");
        return false;
    }

    /// <summary>Копирование отмеченных файлов на отмеченные серверы этой вкладки.</summary>
    /// <param name="notify">
    /// Сообщить об окончании (автокопия вывода). «Выполнить везде» сообщает само — один раз по сводке.
    /// </param>
    /// <param name="approved">Умный режим уже спросил («Выполнить везде» спрашивает за все вкладки разом).</param>
    public async Task<bool> RunCopyAsync(bool notify = true, bool approved = false)
    {
        if (CopyBatch.IsRunning)
        {
            // метку «идёт» не сбиваем — операция та же самая и она ещё работает
            AppLog.Hint("Копирование уже идёт.", LogLevel.Warning);
            return false;
        }

        if (CopyInputs("копирование", "копировать") is not (var jobs, var targets))
        {
            return false;
        }

        // умный режим: сервер непривычный для этой вкладки — сначала спросить
        if (!approved)
        {
            // связка — одним вопросом и до начала копирования: после заливки человек мог уже отойти
            var check = RunHistory.CheckCopy(this, targets).With(ChainCheck(targets));

            if (!RunHistory.Confirm(check))
            {
                return false;
            }

            RunHistory.Commit(check);
        }

        var settings = AppState.Current.Settings;

        // без переноса даты у файла на сервере стоит время заливки, а не оригинала, — тогда
        // «другая дата» будет у всего подряд и отправляться будет всё. Молчать об этом нельзя
        if (jobs.Any(j => j.Options.MatchByDate && !j.Options.MatchByHash && !j.Options.PreserveTimestamps))
        {
            AppLog.Warn(OnTab(
                "Замена по дате изменения без переноса даты на сервер ничего не отсеет: "
                + "у серверных файлов стоит время заливки. Включите «сохранять дату изменения»"));
        }

        // путь ставим после правки истории: выпадающий список чистит поле ввода, когда из него
        // пропадает выбранная строка, и пустое поле уходит в окно — так путь и терялся
        KeepWindowPaths(() =>
        {
            foreach (var job in jobs)
            {
                settings.LastRemotePath = job.RemotePath;
                settings.PushHistory(settings.RemotePathHistory, job.RemotePath);
            }

            AppState.Current.SaveSettings();
        });

        foreach (var job in jobs)
        {
            job.Window!.RemotePath = job.RemotePath;
        }

        // файлы могли измениться с прошлого раза — считаем хеши заново и обновляем таблицу
        _localHashes.Clear();
        _ = RefreshItemsInfoAsync();

        foreach (var job in jobs)
        {
            AppLog.Started(OnTab((CopyWindows.Count > 1 ? $"Окно {job.Window?.LogName}: копирование " : "Копирование ")
                + $"{job.Payload.Count} объект(ов) в {job.RemotePath}" + UploadTerms(job)), ActionLog.WhereText(targets));
        }

        await UploadJobsAsync(jobs, targets, prepare: ChainPlan());

        // с галочкой «+ команды» автокопия ждёт личные команды: отчёт один, копирование и команды
        var together = _afterCopyEnabled && Personal is not null && AppState.Current.Settings.CopyWithChain;

        if (notify && !together)
        {
            AppState.Current.NotifyFinished(CopyBatch);
        }

        await RunChainAsync(notifyCommands: !together);

        if (notify && together)
        {
            AppState.Current.NotifyFinished(CopyBatch);
        }

        return true;
    }

    /// <summary>
    /// Что копировать, куда и на какие серверы — одни проверки у копирования и сравнения.
    /// Null — делать нечего, причина уже показана.
    /// </summary>
    /// <param name="what">Название действия для метки на ярлыке: «копирование», «сравнение».</param>
    /// <param name="verb">Глагол для причины: «копировать нечего», «сравнивать нечего».</param>
    private (List<UploadJob> Jobs, IReadOnlyList<ServerEntry> Targets)? CopyInputs(string what, string verb)
    {
        // окна без отмеченных файлов просто пропускаются, как пустые окна команд
        var windows = CopyWindows.Where(w => w.Items.Any(i => i.IsSelected)).ToList();

        if (windows.Count == 0)
        {
            Refuse(what, !AllItems.Any()
                ? $"Список файлов пуст — {verb} нечего."
                : $"Ни один объект не отмечен галочкой — {verb} нечего.");
            return null;
        }

        var jobs = new List<UploadJob>();

        foreach (var window in windows)
        {
            var remotePath = SshService.NormalizeRemote(window.RemotePath);

            if (string.IsNullOrWhiteSpace(remotePath) || remotePath == "/")
            {
                var where = CopyWindows.Count > 1 ? $"Окно {window.LogName}: не" : "Не";
                Refuse(what, $"{where} указан путь на сервере — укажите каталог, корень «/» не принимается.");
                return null;
            }

            jobs.Add(new UploadJob(
                window,
                window.Items.Where(i => i.IsSelected).ToList(),
                remotePath,
                UploadOptionsOf(window),
                OptionsOf(window).VerifyChecksums));
        }

        // у каждого окна копирования свои отмеченные серверы
        var used = new HashSet<ServerEntry>();

        foreach (var job in jobs)
        {
            var window = job.Window!;
            var own = TargetsOf(window);

            if (own.Count == 0)
            {
                Refuse(what, WindowServers
                    ? $"Окно {window.LogName}: не отмечено ни одного сервера — отметьте их слева, выбрав номер окна."
                    : $"Не отмечено ни одного сервера — {verb} не с чем.");
                return null;
            }

            used.UnionWith(own);
        }

        // порядок — как в панели слева
        var targets = CopyServers.Select(c => c.Server).Where(used.Contains).ToList();
        return (jobs, targets);
    }

    /// <summary>
    /// Строки запуска по порядку: на каждом сервере у каждого окна своя. Окно ниже на том же сервере идёт
    /// после окна выше, если <paramref name="chained"/>. Команды после копирования встают под последним окном сервера.
    /// </summary>
    private Action<OperationResult> WindowRows<T>(
        List<(ServerEntry Server, T Job)> rows,
        Func<T, CopyWindow?> windowOf,
        Action<OperationResult>? prepare,
        bool chained)
    {
        var index = 0;
        var last = new Dictionary<ServerEntry, OperationResult>();

        return row =>
        {
            var at = index++;
            var (server, job) = rows[at];

            row.Job = job;

            if (CopyWindows.Count > 1 && windowOf(job) is { } window)
            {
                row.WindowLabel = window.LogName;
                window.IsFailed = false;
            }

            if (chained)
            {
                row.PreviousWindow = last.GetValueOrDefault(server);
                last[server] = row;
            }

            if (rows.FindLastIndex(r => ReferenceEquals(r.Server, server)) == at)
            {
                prepare?.Invoke(row);
            }
        };
    }

    /// <summary>Окно встало на суммах. Работа идёт не в потоке окна.</summary>
    private static void FlashFailure(CopyWindow? window)
    {
        if (window is not null)
        {
            Application.Current?.Dispatcher.BeginInvoke(() => window.IsFailed = true);
        }
    }

    private UploadOptions UploadOptionsOf(CopyWindow window)
    {
        var options = OptionsOf(window);

        return new UploadOptions(
            options.Overwrite,
            options.PreserveTimestamps,
            options.MatchByDate,
            options.MatchByHash,
            options.ExcludeMasks,
            options.UseGlobalExcludes ? AppState.Current.GlobalExcludeMasks : string.Empty);
    }

    /// <summary>Что копирует одно окно. У «Проводника» окна нет — там задание одно и без номера.</summary>
    public sealed record UploadJob(
        CopyWindow? Window,
        IReadOnlyList<TransferItem> Payload,
        string RemotePath,
        UploadOptions Options,
        bool Verify);

    /// <summary>Начало названия операции сравнения — по нему окончание отличают от копирования.</summary>
    public const string CompareName = "Сравнение";

    /// <summary>
    /// «Сравнить» на «Копировании»: сверка отмеченных файлов с сервером по правилу из параметров —
    /// вывод тот же, что у копирования, только ничего не копируется. Умный режим не спрашивает:
    /// на сервере ничего не меняется.
    /// </summary>
    /// <param name="notify">Сообщить об окончании (автокопия вывода). «Везде» сообщает само — по сводке.</param>
    public async Task<bool> RunCompareCopyAsync(bool notify = true)
    {
        if (CopyBatch.IsRunning)
        {
            AppLog.Hint("Копирование или сравнение уже идёт.", LogLevel.Warning);
            return false;
        }

        if (CopyInputs("сравнение", "сравнивать") is not (var jobs, var targets))
        {
            return false;
        }

        var settings = AppState.Current.Settings;

        // файлы могли измениться с прошлого раза — суммы заново, как перед копированием
        _localHashes.Clear();
        _ = RefreshItemsInfoAsync();

        foreach (var job in jobs)
        {
            AppLog.Started(OnTab((CopyWindows.Count > 1 ? $"Окно {job.Window?.LogName}: сравнение " : "Сравнение ")
                + $"{job.Payload.Count} объект(ов) с {job.RemotePath} — ничего не копируется"), ActionLog.WhereText(targets));
        }

        var rows = targets.SelectMany(server => jobs.Where(job => Goes(job, server)).Select(job => (Server: server, Job: job))).ToList();

        // сошлось на всех серверах — файл и так там, отмечать его незалитым незачем
        var dates = RowDates(jobs);
        var matched = new System.Collections.Concurrent.ConcurrentDictionary<TransferItem, int>();
        var differs = new System.Collections.Concurrent.ConcurrentDictionary<TransferItem, bool>();

        // свитч включён — сверяется только незалитое внутри строк, как и при копировании; всё — со свитчем выключенным
        var trustSent = UnsentHeld;

        await CompareCoreAsync(
            rows.Select(r => r.Server).ToList(),
            async (server, result, ct) =>
            {
                var job = (UploadJob)result.Job!;

                result.Append(BlockParams);
                result.Append($"Сравнение с {job.RemotePath} — ничего не копируется");
                result.Append($"объектов: {job.Payload.Count}");
                result.Append("── сравнение ──");

                await Task.Run(() => dates[job].Value, ct);

                var stats = await SshService.CompareUploadAsync(
                    server,
                    settings,
                    job.Payload,
                    job.RemotePath,
                    trustSent ? job.Options with { Settled = SettledOn(job, server) } : job.Options,
                    _localHashes,
                    line => result.Append(line),
                    ct,
                    step => result.Message = (step switch
                    {
                        UploadStep.CheckingHashes => StepCheckingHashes,
                        UploadStep.CheckingDates => StepCheckingDates,
                        _ => StepConnecting,
                    }) + "…");

                // строка сошлась, если отличия не в ней: разница в соседней папке её не держит
                var root = SshService.NormalizeRemote(job.RemotePath);

                foreach (var item in job.Payload)
                {
                    if (Touched(stats, SshService.CombineRemote(root, item.Name)))
                    {
                        differs[item] = true;
                    }
                    else if (job.Options.CompareConfirms)
                    {
                        matched.AddOrUpdate(item, 1, (_, count) => count + 1);
                    }
                }

                return stats;
            },
            "на сервере",
            WindowRows(rows, j => j.Window, null, chained: false));

        // сверка нашла разницу — строка незалитая, что бы ни помнила дата
        foreach (var item in differs.Keys)
        {
            item.Memory.Date = null;
        }

        RememberSent(jobs, dates, (job, item) => matched.GetValueOrDefault(item) == targets.Count(s => Goes(job, s)), targets);
        await RemarkUnsentAsync();

        if (notify)
        {
            AppState.Current.NotifyFinished(CopyBatch);
        }

        return true;
    }

    /// <summary>
    /// «Сравнить» на «Скачивании»: сверка файлов сервера с тем, что лежит в папке здесь, по правилу
    /// из параметров. Вывод тот же, что у скачивания, только ничего не скачивается.
    /// </summary>
    /// <param name="notify">Сообщить об окончании (автокопия вывода). «Везде» сообщает само — по сводке.</param>
    public async Task<bool> RunCompareDownloadAsync(bool notify = true)
    {
        if (CopyBatch.IsRunning)
        {
            AppLog.Hint("Скачивание или сравнение уже идёт.", LogLevel.Warning);
            return false;
        }

        if (DownloadInputs("сравнение", "сравнивать", create: false) is not { } jobs)
        {
            return false;
        }

        var settings = AppState.Current.Settings;

        foreach (var job in jobs)
        {
            AppLog.Started(OnTab((CopyWindows.Count > 1 ? $"Окно {job.Window?.LogName}: сравнение" : "Сравнение")
                + $": путей {job.Plan.Values.Sum(p => p.Count)} и папка {job.Folder} — ничего не скачивается"), ActionLog.WhereText(job.Plan.Keys));
        }

        var rows = jobs.SelectMany(j => j.Plan.Keys).Distinct()
            .SelectMany(server => jobs.Where(j => j.Plan.ContainsKey(server)).Select(job => (Server: server, Job: job)))
            .ToList();

        // сошлось — файл и так здесь, отмечать его нескачанным незачем
        var dates = DownloadDates(jobs);

        await CompareCoreAsync(
            rows.Select(r => r.Server).ToList(),
            async (server, result, ct) =>
            {
                var job = (DownloadJob)result.Job!;
                var root = job.Plan.Count > 1 ? Path.Combine(job.Folder, FolderName(server)) : job.Folder;

                result.Append(BlockParams);
                result.Append($"Сравнение с {root} — ничего не скачивается");
                result.Append($"путей: {job.Plan[server].Count}");
                result.Append("── сравнение ──");

                var stats = await DownloadService.CompareAsync(server, settings, job.Plan[server], root, job.Options, line => result.Append(line), ct);

                RememberReceived(dates, job, server, stats);

                return stats;
            },
            "здесь",
            WindowRows(rows, j => j.Window, null, chained: false));

        if (dates.Count > 0)
        {
            Save();
            await RemarkUnsentAsync();
        }

        if (notify)
        {
            AppState.Current.NotifyFinished(CopyBatch);
        }

        return true;
    }

    /// <summary>
    /// Сравнение — одно для копирования и скачивания: строка на сервер, итог «сошлось / не сошлось»
    /// в статусе, разбор — в выводе, коротко — на ярлыке.
    /// </summary>
    /// <param name="where">Где может не быть файла: «на сервере» или «здесь».</param>
    private async Task CompareCoreAsync(
        IReadOnlyList<ServerEntry> targets,
        Func<ServerEntry, OperationResult, CancellationToken, Task<CompareStats>> compare,
        string where,
        Action<OperationResult> prepare)
    {
        var settings = AppState.Current.Settings;
        var gate = new Lock();
        var worst = 0;
        var worstOn = new Dictionary<ServerEntry, int>();

        SetScope();
        ShowStatus("сравнение", string.Empty, WorkspaceStatus.Running);
        AppState.Current.RequestTerminal();
        ShowResults(CopyBatch);

        await CopyBatch.RunAsync(
            Operation(CompareName),
            targets,
            async (server, result, ct) =>
            {
                result.Message = StepConnecting + "…";

                var stats = await compare(server, result, ct);

                // окна сервера складываются: на ярлыке — все его отличия
                lock (gate)
                {
                    worstOn[server] = worstOn.GetValueOrDefault(server) + stats.Mismatches;
                    worst = Math.Max(worst, worstOn[server]);
                }

                ReportCompare(result, stats, where);
            },
            settings.MaxParallel,
            prepare: prepare);

        if (CopyBatch.IsRunning)
        {
            return;
        }

        FinishStatus(
            CopyBatch,
            "сравнение",
            worst == 0 ? "сошлось" : $"отличий {worst}",
            worst == 0 ? "по выбранному правилу отличий нет" : "что именно не сошлось — в выводе журнала");
    }

    /// <summary>Сколько отличий, не влияющих на итог, показать в выводе: дата может разойтись у всех файлов сразу.</summary>
    private const int IgnoredShown = 200;

    /// <summary>Лежащее на сервере сверх сравниваемого — только список, на итог не влияет. Папка — одной строкой.</summary>
    private static void AppendExtras(OperationResult result, IReadOnlyList<CompareExtra> extras)
    {
        if (extras.Count == 0)
        {
            return;
        }

        result.Append($"── ещё на сервере в этих папках, не сравнивалось ({extras.Count}) ──");

        foreach (var extra in extras.Take(IgnoredShown))
        {
            result.Append(extra.IsDirectory ? $"· {extra.Path}/  (файлов внутри: {extra.Files})" : $"· {extra.Path}");
        }

        if (extras.Count > IgnoredShown)
        {
            result.Append($"…и ещё {extras.Count - IgnoredShown}");
        }
    }

    /// <summary>
    /// Итог сравнения в строку сервера. Статус — сразу ответ «сошлось или нет»; в выводе — счёт
    /// и списки: чего нет, что отличается и чем. «Не сошлось» — не ошибка, а ответ: оранжевым.
    /// Отличия, которые выбранный режим не сверяет, на статус не влияют и идут только в вывод.
    /// </summary>
    private static void ReportCompare(OperationResult result, CompareStats stats, string where)
    {
        result.Append("── итог сравнения ──");
        result.Append($"совпало файлов: {stats.Same} из {stats.Total}");

        if (stats.MissingFiles.Count > 0)
        {
            result.Append($"нет {where} файлов: {stats.MissingFiles.Count}");
        }

        if (stats.Different.Count > 0)
        {
            result.Append($"отличаются: {stats.Different.Count}");
        }

        if (stats.MissingDirs.Count > 0)
        {
            result.Append($"нет {where} папок: {stats.MissingDirs.Count}");
        }

        if (stats.Ignored.Count > 0)
        {
            result.Append($"на итог не влияют: {stats.Ignored.Count}");
        }

        var extras = stats.Extras ?? [];

        if (extras.Count > 0)
        {
            result.Append($"ещё на сервере в этих папках, не сравнивалось: {extras.Count}");
        }

        result.Append("время: " + stats.Clock);

        if (stats.MissingDirs.Count > 0)
        {
            result.Append($"── нет {where} папок ({stats.MissingDirs.Count}) ──");

            foreach (var dir in stats.MissingDirs)
            {
                result.Append($"→ {dir}/");
            }
        }

        if (stats.MissingFiles.Count > 0)
        {
            result.Append($"── нет {where} файлов ({stats.MissingFiles.Count}) ──");

            foreach (var file in stats.MissingFiles)
            {
                result.Append($"→ {file}");
            }
        }

        if (stats.Different.Count > 0)
        {
            result.Append($"── отличаются ({stats.Different.Count}) ──");

            foreach (var diff in stats.Different)
            {
                result.Append($"≠ {diff.Path} · {diff.Reason}");
            }
        }

        if (stats.Ignored.Count > 0)
        {
            result.Append($"── на итог не влияют ({stats.Ignored.Count}) ──");

            foreach (var diff in stats.Ignored.Take(IgnoredShown))
            {
                result.Append($"≈ {diff.Path} · {diff.Reason}");
            }

            if (stats.Ignored.Count > IgnoredShown)
            {
                result.Append($"…и ещё {stats.Ignored.Count - IgnoredShown}");
            }
        }

        AppendExtras(result, extras);

        // среди совпавших есть файлы с отличиями, которые режим не сверяет: «одинаковыми» их не назвать
        var hasIgnored = stats.Ignored.Count > 0;

        if (stats.Mismatches == 0)
        {
            result.Message = stats.Total == 0 ? "сошлось · файлов нет"
                : hasIgnored ? $"сошлось · файлов {stats.Same}"
                : $"сошлось · одинаковых файлов {stats.Same}";
            result.Status = OperationStatus.Success;
            return;
        }

        var parts = new List<string>();

        if (stats.MissingFiles.Count > 0)
        {
            parts.Add($"нет {where} {stats.MissingFiles.Count}");
        }

        if (stats.Different.Count > 0)
        {
            parts.Add($"отличаются {stats.Different.Count}");
        }

        if (stats.MissingDirs.Count > 0)
        {
            parts.Add($"папок нет {stats.MissingDirs.Count}");
        }

        result.Message = "не сошлось · " + string.Join(" · ", parts) + (hasIgnored ? $" · совпало {stats.Same}" : $" · одинаковых {stats.Same}");
        result.Warning = true;
        result.Status = OperationStatus.Success;
    }

    /// <summary>Параметры захода; исключения — свои и общие порознь, чтобы в выводе было видно, что чем отсеяно.</summary>
    public UploadOptions CurrentOptions =>
        new(OverwriteFiles, PreserveTimestamps, MatchByDate, MatchByHash, ExcludeMasks, GlobalExcludeMasks);

    /// <summary>
    /// Заливка — одна на всю программу: её зовут и «Копирование», и «Проводник». Подключение
    /// к каждому серверу, отправка, сверка, строка времени, полоса передачи, статус на ярлыке.
    /// Всё, что улучшается в копировании, достаётся обоим разделам сразу.
    /// </summary>
    public Task UploadCoreAsync(
        IReadOnlyList<TransferItem> payload,
        string remotePath,
        IReadOnlyList<ServerEntry> targets,
        UploadOptions options,
        bool verify,
        ConfirmReplace? confirmReplace = null,
        bool concurrent = false,
        Action<OperationResult>? prepare = null) =>
        UploadJobsAsync([new UploadJob(null, payload, remotePath, options, verify)], targets, confirmReplace, concurrent, prepare);

    /// <summary>
    /// Задания идут на каждом сервере по порядку: следующее окно — после успешного предыдущего,
    /// не дожидаясь остальных серверов. Встало на сервере — дальше на нём не идёт.
    /// </summary>
    private async Task UploadJobsAsync(
        IReadOnlyList<UploadJob> jobs,
        IReadOnlyList<ServerEntry> targets,
        ConfirmReplace? confirmReplace = null,
        bool concurrent = false,
        Action<OperationResult>? prepare = null)
    {
        var settings = AppState.Current.Settings;
        var rows = targets.SelectMany(server => jobs.Where(job => Goes(job, server)).Select(job => (Server: server, Job: job))).ToList();

        SetScope();
        ShowStatus(StepConnecting, string.Empty, WorkspaceStatus.Running);
        AppState.Current.RequestTerminal();
        ShowResults(CopyBatch);

        // Ярлык показывает один короткий шаг — что идёт прямо сейчас. Серверы работают
        // не строем, поэтому шаг только двигается вперёд: мигание «сверка даты ⇄ копирование»
        // читать невозможно.
        var stepLock = new object();
        var reachedStep = (int)UploadStep.Connecting;

        void Step(int order, string text)
        {
            lock (stepLock)
            {
                if (order <= reachedStep)
                {
                    return;
                }

                reachedStep = order;
            }

            ShowStatus(text, string.Empty, WorkspaceStatus.Running);
        }

        // на ярлыке — файлы, а не серверы: набор файлов у серверов один и тот же,
        // поэтому берём самый «загруженный» сервер, а расхождения видно в терминале
        var numbersLock = new object();
        var sentMax = 0;
        var seenMax = 0;
        var sentOn = new Dictionary<ServerEntry, int>();
        var seenOn = new Dictionary<ServerEntry, int>();

        // для «Незалитых»: дата строк до отправки — правка по ходу копирования останется незалитой.
        // Запоминается, только если окно дошло до всех серверов целиком
        var dates = RowDates(jobs);
        var delivered = new System.Collections.Concurrent.ConcurrentDictionary<UploadJob, int>();

        // свитч включён — проверенное прошлой заливкой сервер не перечитывает; всё сверить — свитч выключить
        var trustSent = UnsentHeld;

        await CopyBatch.RunAsync(
            Operation("Копирование"),
            rows.Select(r => r.Server).ToList(),
            async (server, result, ct) =>
            {
                var job = (UploadJob)result.Job!;
                var (_, payload, remotePath, options, verify) = job;

                var sentTotal = 0;
                var seenTotal = 0;
                var upToDateTotal = 0;
                var skippedTotal = 0;
                var bytesTotal = 0L;
                var verified = false;

                string Summary() =>
                    $"отправлено {sentTotal}, {TransferItem.Format(bytesTotal)}"
                    + (upToDateTotal > 0 ? $" · без изменений {upToDateTotal}" : string.Empty)
                    + (skippedTotal > 0 ? $" · уже есть {skippedTotal}" : string.Empty)
                    + (verified ? ", суммы сошлись" : string.Empty);

                result.Message = StepConnecting + "…";
                result.Append(BlockParams);
                result.Append($"Назначение: {remotePath}");
                result.Append($"объектов: {payload.Count}" + UploadTerms(job));
                result.Append("── копирование ──");

                await Task.Run(() => dates[job].Value, ct);

                var stats = await SshService.UploadAsync(
                    server,
                    settings,
                    payload,
                    remotePath,
                    trustSent ? options with { Settled = SettledOn(job, server) } : options,
                    _localHashes,
                    line => result.Append(line),
                    ct,
                    step =>
                    {
                        var text = step switch
                        {
                            UploadStep.Connecting => StepConnecting,
                            UploadStep.CheckingHashes => StepCheckingHashes,
                            UploadStep.CheckingDates => StepCheckingDates,
                            _ => StepCopying,
                        };

                        result.Message = text + "…";
                        Step((int)step, text);
                    },
                    result.SetTransfer,
                    confirmReplace);

                sentTotal += stats.FilesSent;
                seenTotal += stats.FilesSent + stats.FilesUpToDate + stats.FilesSkipped;
                upToDateTotal += stats.FilesUpToDate;
                skippedTotal += stats.FilesSkipped;
                bytesTotal += stats.BytesSent;

                // на ярлыке — итог самого загруженного сервера по всем его окнам
                lock (numbersLock)
                {
                    sentOn[server] = sentOn.GetValueOrDefault(server) + stats.FilesSent;
                    seenOn[server] = seenOn.GetValueOrDefault(server) + stats.FilesSent + stats.FilesUpToDate + stats.FilesSkipped;
                    sentMax = Math.Max(sentMax, sentOn[server]);
                    seenMax = Math.Max(seenMax, seenOn[server]);
                }

                // подробный разбор — в вывод, коротко — в строку сервера
                result.Append("── итог ──");
                result.Append($"отправлено файлов: {stats.FilesSent} ({TransferItem.Format(stats.BytesSent)})");

                if (options.Matching)
                {
                    result.Append($"пропущено, на сервере уже такой же: {stats.FilesUpToDate}");

                    if (stats.FilesRedated > 0)
                    {
                        result.Append($"  из них дата поправлена: {stats.FilesRedated}");
                    }
                }

                if (!options.Overwrite || stats.FilesSkipped > 0)
                {
                    result.Append($"пропущено, файл уже есть: {stats.FilesSkipped}");
                }

                result.Append($"создано папок: {stats.DirsCreated}");
                result.Append($"всего рассмотрено файлов: {stats.FilesSent + stats.FilesUpToDate + stats.FilesSkipped}");

                // отправленное — отдельным списком в самом низу: в ходе копирования эти строки
                // теряются среди пропусков и замен. Сверка и время — над ним, а не под
                // тысячами строк, где их не найти
                void AppendFiles()
                {
                    if (stats.CreatedDirs.Count > 0)
                    {
                        result.Append($"── созданы папки ({stats.CreatedDirs.Count}) ──");

                        foreach (var dir in stats.CreatedDirs)
                        {
                            result.Append($"→ {dir}/");
                        }
                    }

                    if (stats.Files.Count > 0)
                    {
                        result.Append($"── скопированы файлы ({stats.Files.Count}, {TransferItem.Format(stats.BytesSent)}) ──");

                        foreach (var file in stats.Files)
                        {
                            result.Append($"→ {file.RemotePath}  ({TransferItem.Format(file.Size)})");
                        }
                    }
                    else
                    {
                        result.Append("── скопированных файлов нет ──");
                    }
                }

                if (verify && stats.Files.Count > 0)
                {
                    result.Message = StepVerifying + "…";
                    Step(StepVerifyingOrder, StepVerifying);

                    // передача кончилась — полоса ушла бы стоять на 100%, пока идёт сверка
                    result.SetTransfer(0, 0);
                    stats.Clock.Restart();

                    var mismatches = await SshService.VerifyChecksumsAsync(
                        server, settings, stats.Files, _localHashes, line => result.Append(line), ct);

                    stats.Clock.Mark("сверка после");
                    result.Append("время: " + stats.Clock);

                    if (mismatches > 0)
                    {
                        AppendFiles();
                        result.Message = Summary() + $", РАСХОЖДЕНИЙ: {mismatches}";
                        result.Status = OperationStatus.Failed;
                        FlashFailure(job.Window);
                        return;
                    }

                    result.Append($"контрольные суммы совпали: {stats.Files.Count} файл(ов)");
                    verified = true;
                }
                else
                {
                    result.Append("время: " + stats.Clock);
                }

                AppendFiles();

                result.Message = Summary();
                result.Status = OperationStatus.Success;

                // «файл уже есть, не заменён» — на сервер ушло не всё
                if (stats.FilesSkipped == 0)
                {
                    delivered.AddOrUpdate(job, 1, (_, count) => count + 1);
                }
            },
            settings.MaxParallel,
            concurrent: concurrent,
            prepare: WindowRows(rows, j => j.Window, prepare, chained: true));

        RememberSent(jobs, dates, (job, _) => delivered.GetValueOrDefault(job) == targets.Count(s => Goes(job, s)), targets);

        // запуски «Проводника» идут рядом — итог на ярлыке, когда закончился последний
        if (CopyBatch.IsRunning)
        {
            return;
        }

        // итог задачи, а не число серверов: по «0 из 5» сразу видно, что ничего не ушло.
        // Слово «скопировано» не пишем — на узкой плашке оно не влезает, а смысл и так ясен
        var considered = seenMax > 0 ? seenMax : jobs.Sum(j => j.Payload.Count);

        FinishStatus(
            CopyBatch,
            "скопировано",
            $"{sentMax} из {considered}",
            sentMax == 0 && CopyBatch.RunFailed == 0
                ? "отправлять было нечего: на серверах файлы такие же или свежее"
                : null);
    }

    /// <summary>Проверка умного режима для личных команд — если они выполнятся после копирования.</summary>
    private RunHistory.SmartCheck? ChainCheck(IReadOnlyList<ServerEntry> targets)
    {
        if (!_afterCopyEnabled || Personal is not { } personal)
        {
            return null;
        }

        var steps = personal.RunnableSteps(null, onlyThis: false);
        return steps.Count == 0 || targets.Count == 0 ? null : RunHistory.CheckCommands(personal, personal.Numbered(steps), targets);
    }

    /// <summary>
    /// Строки команд после копирования — под строкой копирования каждого сервера, где они пойдут,
    /// сразу со стартом копирования: видно, что будет дальше, и любую можно остановить заранее.
    /// Null — команды после копирования не пойдут.
    /// </summary>
    private Action<OperationResult>? ChainPlan()
    {
        if (!_afterCopyEnabled || Personal is not { } personal || personal.CommandBatch.IsRunning)
        {
            return null;
        }

        var steps = personal.RunnableSteps(null, onlyThis: false).Select(s => (s.Number, s.Text)).ToList();

        if (steps.Count == 0)
        {
            return null;
        }

        return row => row.PlanSteps(steps);
    }

    /// <summary>
    /// Личные команды после копирования — на серверах этого копирования, отметки личной вкладки
    /// здесь не действуют; только там, где копирование (и сверка сумм, если она включена) прошло.
    /// Умный режим уже спросил до копирования.
    /// Команды, остановленные заранее в строке терминала, не пойдут — ни они, ни нижние.
    /// </summary>
    /// <param name="notifyCommands">Сообщить об окончании команд отдельно (иначе — вместе с копированием).</param>
    private async Task RunChainAsync(bool notifyCommands)
    {
        if (!_afterCopyEnabled || Personal is not { } personal)
        {
            return;
        }

        if (personal.CommandBatch.IsRunning)
        {
            ChainSkipped("на личной вкладке команды ещё выполняются");
            return;
        }

        // свитч включён не просто так: чего не хватает, говорим прямо там, где идёт вывод копирования
        if (personal.RunnableSteps(null, onlyThis: false).Count == 0)
        {
            ChainSkipped($"в личной вкладке команд нет ни одной команды — впишите её (или выключите свитч на ярлыке «{Title}»)");
            return;
        }

        // только последний запуск: таблица копирования могла накопить и прошлые
        var failed = CopyBatch.LastRun
            .Where(r => r.Status != OperationStatus.Success)
            .Select(r => r.Server)
            .ToHashSet();

        // где копирование не прошло, команды не пойдут — их строки «не запускалась»
        foreach (var row in CopyBatch.LastRun.Where(r => failed.Contains(r.Server)))
        {
            row.SkipPlanned();
        }

        // заранее поставленные строки команд переходят к цепочке; все остановлены — сервер ей не нужен
        var copyRows = CopyBatch.LastRun.GroupBy(r => r.Server).ToDictionary(g => g.Key, g => g.Last());
        var stopped = copyRows.Values.Where(r => r.StepsStopped).Select(r => r.Server).ToHashSet();

        // серверы этого копирования; где оно не прошло, команды не пойдут
        var targets = copyRows.Keys.Where(s => !failed.Contains(s) && !stopped.Contains(s)).ToList();

        // команды остановили сами — не сбой, объяснять нечего
        if (CopyBatch.RunSuccess > 0 && targets.Count == 0 && stopped.Count > 0)
        {
            foreach (var step in personal.Steps)
            {
                step.Clear();
            }

            return;
        }

        if (CopyBatch.RunSuccess == 0 || targets.Count == 0)
        {
            ChainSkipped("копирование не прошло ни на одном сервере");

            foreach (var step in personal.Steps)
            {
                step.Clear();
            }

            return;
        }

        if (failed.Count > 0)
        {
            AppLog.Info(OnTab("Команды после копирования: где копирование не прошло, пропущено"), ActionLog.WhereText(targets));
        }

        await personal.RunCommandAsync(
            notify: notifyCommands,
            approved: true,
            servers: targets,
            prepare: row =>
            {
                if (copyRows.GetValueOrDefault(row.Server) is { } copy)
                {
                    row.AdoptSteps(copy);
                }
            });

        // цепочка отказала или до сервера не дошла — ждать его командам больше нечего
        foreach (var copy in copyRows.Values)
        {
            copy.SkipPlanned();
        }
    }

    /// <summary>
    /// Почему личные команды не пошли — в конец вывода копирования каждого сервера, там,
    /// куда человек смотрит после копирования; в журнале — та же строка для истории.
    /// </summary>
    private void ChainSkipped(string reason)
    {
        var line = "Команды после копирования не запущены: " + reason + ".";

        foreach (var result in CopyBatch.LastRun)
        {
            result.Append(Views.Controls.OutputColoring.WarningMark + line + " —");
            result.SkipPlanned();
        }

        AppLog.Warn(OnTab(line), ActionLog.WhereText(CopyBatch.LastRun.Select(r => r.Server)));
    }

    /// <summary>
    /// Окна, которые пойдут в прогон: с <paramref name="from"/> (или с первого) и ниже,
    /// либо одно это окно. Пустые окна пропускаются.
    /// </summary>
    private List<CommandStep> RunnableSteps(CommandStep? from, bool onlyThis)
    {
        var start = from is null ? 0 : Math.Max(0, Steps.IndexOf(from));
        var chosen = onlyThis ? Steps.Skip(start).Take(1) : Steps.Skip(start);
        return chosen.Where(s => !string.IsNullOrWhiteSpace(s.Text)).ToList();
    }

    /// <summary>Номера окон для умного режима и вывода: при одном окне номер не нужен.</summary>
    private IEnumerable<(int Number, string Text)> Numbered(IEnumerable<CommandStep> steps) =>
        steps.Select(s => (Steps.Count > 1 ? s.Number : 0, s.Text));

    /// <summary>
    /// Для «Выполнить везде»: проверка умного режима без вопроса. null — вкладка всё равно
    /// не запустится (нет команды или серверов) и откажет сама, с причиной на ярлыке.
    /// </summary>
    public RunHistory.SmartCheck? PrecheckCommand()
    {
        var steps = RunnableSteps(null, onlyThis: false);
        var targets = CommandTargets;

        return steps.Count == 0 || targets.Count == 0 || CommandBatch.IsRunning
            ? null
            : RunHistory.CheckCommands(this, Numbered(steps), targets);
    }

    /// <summary>То же для копирования.</summary>
    public RunHistory.SmartCheck? PrecheckCopy()
    {
        var targets = CopyTargets;

        return !AllItems.Any(i => i.IsSelected) || targets.Count == 0 || CopyBatch.IsRunning
            ? null
            : RunHistory.CheckCopy(this, targets).With(ChainCheck(targets));
    }

    /// <summary>
    /// Выполнение команд этой вкладки на её отмеченных серверах. Окон несколько — цепочкой:
    /// на каждом сервере следующее окно начинается после успешного предыдущего, не дожидаясь
    /// остальных серверов. Встало на сервере — дальше на нём не идёт.
    /// </summary>
    /// <param name="notify">
    /// Сообщить об окончании (автокопия вывода). «Выполнить везде» сообщает само — один раз по сводке.
    /// </param>
    /// <param name="from">С какого окна (по умолчанию с первого).</param>
    /// <param name="onlyThis">Только окно <paramref name="from"/>, без нижних.</param>
    /// <param name="approved">Умный режим уже спросил.</param>
    /// <param name="servers">Свои серверы вместо отмеченных — для команд после копирования.</param>
    /// <param name="prepare">Строке сервера до старта — команды после копирования забирают свои заранее поставленные строки.</param>
    public async Task<bool> RunCommandAsync(
        bool notify = true,
        CommandStep? from = null,
        bool onlyThis = false,
        bool approved = false,
        IReadOnlyList<ServerEntry>? servers = null,
        Action<OperationResult>? prepare = null)
    {
        if (CommandBatch.IsRunning)
        {
            AppLog.Hint("Команда уже выполняется.", LogLevel.Warning);
            return false;
        }

        var steps = RunnableSteps(from, onlyThis);

        if (steps.Count == 0)
        {
            return Refuse("команда", onlyThis || Steps.Count == 1 ? "Команда пустая." : "Во всех окнах пусто.");
        }

        var targets = servers ?? CommandTargets;

        if (targets.Count == 0)
        {
            return Refuse("команда", "Не отмечено ни одного сервера для команды.");
        }

        // умный режим: сервер непривычный для этих команд — сначала спросить, одним окном на все
        if (!approved)
        {
            var check = RunHistory.CheckCommands(this, Numbered(steps), targets);

            if (!RunHistory.Confirm(check))
            {
                return false;
            }

            RunHistory.Commit(check);
        }

        var settings = AppState.Current.Settings;

        // первое окно — наверху истории
        foreach (var step in Enumerable.Reverse(steps))
        {
            RememberCommand(step.Text);
        }

        Save();

        // цепочка: ненулевой код останавливает её на сервере. Одиночная команда — как в терминале:
        // что она вернула — её дело, а не ошибка программы
        var chain = steps.Count > 1;
        var numbered = Steps.Count > 1;

        AppLog.Started(
            chain
                ? OnTab($"Цепочка из {steps.Count} окон") + ": " + string.Join(" → ", steps.Select(s => $"#{s.Number} {Preview(s.Text)}"))
                : OnTab($"Команда ({steps[0].Text.Length} символов)" + (steps[0].UseSudo ? ", через sudo" : string.Empty))
                    + ": " + Preview(steps[0].Text),
            ActionLog.WhereText(targets));

        foreach (var step in Steps)
        {
            if (steps.Contains(step))
            {
                step.Begin(targets.Count);
            }
            else
            {
                step.Clear();
            }
        }

        SetScope();
        ShowStatus("выполняется", string.Empty, WorkspaceStatus.Running);
        AppState.Current.RequestTerminal();
        ShowResults(CommandBatch);

        // Ctrl+C посылается отдельным подключением, и под sudo процессы команды принадлежат root
        CommandBatch.UsedSudo = steps.Any(s => s.UseSudo);

        // где цепочка встала на каждом сервере: повтор продолжает с этого окна,
        // уже выполненное на сервере второй раз не запускается
        var resume = new ConcurrentDictionary<ServerEntry, int>();
        var retrying = false;

        void BeforeRetry(IReadOnlyList<ServerEntry> again)
        {
            retrying = true;

            for (var i = 0; i < steps.Count; i++)
            {
                var reaching = again.Count(s => resume.GetValueOrDefault(s) <= i);

                if (reaching > 0)
                {
                    steps[i].Resume(reaching);
                }
            }

            ShowStatus("выполняется", string.Empty, WorkspaceStatus.Running);
        }

        void Finish()
        {
            foreach (var step in steps)
            {
                step.End();
            }

            var passed = steps.Count(s => s.State == StepState.Ok);

            FinishStatus(
                CommandBatch,
                "команда",
                chain
                    ? $"выполнено {passed} из {steps.Count}"
                    : CommandBatch.RunSuccess == 0 ? "не выполнено" : "выполнено");
        }

        await CommandBatch.RunAsync(
            Operation(chain ? "Выполнение цепочки команд" : "Выполнение команды"),
            targets,
            async (server, result, ct) =>
            {
                var index = retrying ? resume.GetValueOrDefault(server) : 0;
                var open = false;

                // строка этой команды — терминал копирования показывает команды по одной
                OperationResult? row = null;
                var rowClock = new System.Diagnostics.Stopwatch();

                // окно на этом сервере закончилось — в счёт окна
                void Close(bool ok)
                {
                    steps[index].ServerFinished(ok);
                    open = false;

                    if (row is not null)
                    {
                        row.Duration = rowClock.Elapsed;
                    }
                }

                // вывод — и в общую строку сервера, и в строку команды
                void Write(string line)
                {
                    result.Append(line);
                    row?.Append(line);
                }

                try
                {
                    for (; index < steps.Count; index++)
                    {
                        var step = steps[index];
                        var label = numbered ? $"#{step.Number} " : string.Empty;
                        var clock = System.Diagnostics.Stopwatch.StartNew();

                        // остановили в строке терминала, пока ждала: не пойдёт ни она, ни нижние
                        if (result.StepStopped(step.Number))
                        {
                            Write($"— #{step.Number} отменена: следующие окна на этом сервере не запускаются —");
                            result.Message = label + "отменено";
                            result.Status = OperationStatus.Cancelled;
                            step.ServerSkipped();
                            return;
                        }

                        // процесс прошлого окна уже кончился — Ctrl+C не должен уйти в него
                        result.ResetJob();
                        step.ServerStarted();
                        open = true;

                        if (numbered)
                        {
                            result.Append($"───── #{step.Number} · {DateTime.Now:dd.MM.yyyy HH:mm:ss} ─────");
                        }

                        var current = row = result.BeginStep(step.Number, step.Text);
                        rowClock.Restart();

                        Write(step.UseSudo ? "── команда, через sudo ──" : "── команда ──");

                        foreach (var line in step.Text.Replace("\r\n", "\n").Split('\n'))
                        {
                            Write("$ " + line);
                        }

                        Write("── вывод ──");

                        // в команде есть таймер — отсчёт в углу поля вывода; у следующего окна цепочки свой
                        var timer = CommandTimer.Seconds(step.Text);
                        result.StartCountdown(timer);
                        current.StartCountdown(timer);

                        result.Message = label + "выполняется…";

                        var outcome = await SshService.RunScriptAsync(
                            server,
                            settings,
                            step.Text,
                            step.UseSudo,
                            Write,
                            ct,
                            (pid, group) =>
                            {
                                result.RemotePid = pid;
                                result.RemoteGroup = group;
                            },
                            input => result.Input = input,
                            waiting =>
                            {
                                result.IsWaitingInput = waiting;
                                result.Message = label + (waiting ? "ждёт ответа" : "выполняется…");
                                current.Message = waiting ? "ждёт ответа" : string.Empty;

                                if (waiting)
                                {
                                    AppLog.Warn(
                                        OnTab("Ждёт ответа — введите его в строке под выводом в журнале"),
                                        server.Display);
                                }
                            },
                            tail =>
                            {
                                result.SetTail(tail);
                                current.SetTail(tail);
                            });

                        // оборвали нашей же кнопкой — это не ошибка, а выполненная просьба.
                        // Проверяется до всего прочего: после kill сервер может и не успеть
                        // сообщить, каким сигналом всё закончилось
                        if (result.Interrupted && !outcome.TimedOut)
                        {
                            Write("— команда прервана —");
                            result.Message = label + "прервано";
                            result.Status = OperationStatus.Cancelled;
                            current.Status = OperationStatus.Cancelled;
                            Close(false);
                            return;
                        }

                        // Ошибка — это когда команду не удалось выполнить: не достучались, оборвало,
                        // не дождались. Что команда написала в вывод — её дело
                        if (outcome.Interruption is { } trouble)
                        {
                            Write("— " + trouble + " —");
                            var why = outcome.TimedOut
                                ? $"не дождались ответа ({settings.CommandTimeoutSeconds} с)"
                                : trouble;
                            result.Message = label + why;
                            result.Status = OperationStatus.Failed;
                            current.Message = why;
                            current.Status = OperationStatus.Failed;
                            Close(false);
                            return;
                        }

                        if (chain && outcome.ExitCode is int code && code != 0)
                        {
                            var last = index == steps.Count - 1;
                            Write(last
                                ? $"— #{step.Number} завершилась с кодом {code} —"
                                : $"— #{step.Number} завершилась с кодом {code}: следующие окна на этом сервере не запускаются —");
                            result.Message = $"#{step.Number}: код {code}";
                            result.Status = OperationStatus.Failed;
                            current.Message = $"код {code} · {current.StepCommand}";
                            current.Status = OperationStatus.Failed;
                            Close(false);
                            return;
                        }

                        Close(true);

                        if (numbered)
                        {
                            Write($"— #{step.Number} ок · {clock.Elapsed:m\\:ss} —");
                        }

                        current.Status = OperationStatus.Success;
                    }

                    result.Message = string.Empty;
                    result.Status = OperationStatus.Success;
                }
                finally
                {
                    // сбой подключения или отмена посреди окна: окно не прошло, нижние не начинались
                    if (open)
                    {
                        steps[index].ServerFinished(false);
                    }

                    if (row is { IsRunning: true })
                    {
                        row.Duration = rowClock.Elapsed;
                        row.Status = ct.IsCancellationRequested ? OperationStatus.Cancelled : OperationStatus.Failed;
                    }

                    for (var rest = index + 1; rest < steps.Count; rest++)
                    {
                        steps[rest].ServerSkipped();

                        // в терминале копирования — «#3 не запускалась»: видно, что не успело
                        result.SkipStep(steps[rest].Number, steps[rest].Text);
                    }

                    // заранее поставленная строка, до которой так и не дошло (оборвалось подключение)
                    result.SkipPlanned();

                    resume[server] = index;
                }
            },
            settings.MaxParallel,
            BeforeRetry,
            Finish,
            prepare: prepare);

        Finish();

        if (notify)
        {
            AppState.Current.NotifyFinished(CommandBatch);
        }

        return true;
    }

    /// <summary>
    /// Проверка связи: uname и uptime. Со страницы «Серверы» приходят серверы, отмеченные
    /// в таблице набора; без списка — отмеченные в панели слева.
    /// </summary>
    public async Task<bool> RunCheckAsync(IReadOnlyList<ServerEntry>? picked = null)
    {
        var targets = (picked ?? SelectedServers).Where(s => !string.IsNullOrWhiteSpace(s.Host)).ToList();

        if (targets.Count == 0)
        {
            return Refuse("связь", "Не отмечено ни одного сервера — проверять нечего.");
        }

        // идёт рядом с прошлой проверкой; сервер, который ещё проверяется, второй раз не ставим
        var checking = CheckBatch.Results.Where(r => r.IsRunning).Select(r => r.Server).ToList();
        targets.RemoveAll(checking.Contains);

        if (targets.Count == 0)
        {
            AppLog.Hint("Эти серверы уже проверяются.", LogLevel.Warning);
            return false;
        }

        var settings = AppState.Current.Settings;

        SetScope();
        ShowStatus("проверка связи", string.Empty, WorkspaceStatus.Running);
        AppState.Current.RequestTerminal();
        ShowResults(CheckBatch);

        await CheckBatch.RunAsync(
            Operation("Проверка связи"),
            targets,
            async (server, result, ct) =>
            {
                result.Message = "подключение…";

                var outcome = await SshService.RunCommandAsync(server, settings, "uname -a; uptime", ct);

                if (!string.IsNullOrWhiteSpace(outcome.StdOut))
                {
                    result.Append(outcome.StdOut.TrimEnd());
                }

                if (!string.IsNullOrWhiteSpace(outcome.StdErr))
                {
                    result.Append(outcome.StdErr.TrimEnd());
                }

                result.Message = "связь есть";
                result.Status = OperationStatus.Success;
            },
            settings.MaxParallel,
            concurrent: true);

        // итог — когда закончилась последняя из шедших рядом
        if (CheckBatch.IsRunning)
        {
            return true;
        }

        FinishStatus(
            CheckBatch,
            "связь",
            CheckBatch.RunSuccess == 0 ? "связи нет" : "связь есть");

        return true;
    }

    /// <summary>
    /// «Листинг»: ls -la путей на серверах. Вывод — в журнал вкладки строками команды, рядом с копированием;
    /// статус на ярлыке не трогает — он про копирование.
    /// </summary>
    public async Task<bool> RunListingAsync(IReadOnlyList<ServerEntry> servers, IReadOnlyList<string> paths)
    {
        var targets = servers.Where(s => !string.IsNullOrWhiteSpace(s.Host)).ToList();
        var listed = paths.Select(p => p.Trim()).Where(p => p.Length > 0).Distinct().ToList();

        if (listed.Count == 0)
        {
            AppLog.Hint("Путь на сервере пустой — показывать нечего.", LogLevel.Warning);
            return false;
        }

        if (targets.Count == 0)
        {
            AppLog.Hint("Не отмечено ни одного сервера — показывать нечего.", LogLevel.Warning);
            return false;
        }

        var settings = AppState.Current.Settings;
        var command = "ls -la -- " + string.Join(" ", listed.Select(SshService.ShellQuote));

        SetScope();
        AppState.Current.RequestTerminal();
        ShowResults(CommandBatch);

        await CommandBatch.RunAsync(
            Operation("Листинг"),
            targets,
            async (server, result, ct) =>
            {
                result.Append(BlockParams);
                result.Append("Листинг: " + string.Join(", ", listed));
                result.Append("── команда ──");
                result.Append("$ " + command);
                result.Append("── вывод ──");
                result.Message = "подключение…";

                var outcome = await SshService.RunCommandAsync(server, settings, command, ct);

                if (!string.IsNullOrWhiteSpace(outcome.StdOut))
                {
                    result.Append(outcome.StdOut.TrimEnd());
                }

                if (!string.IsNullOrWhiteSpace(outcome.StdErr))
                {
                    result.Append(outcome.StdErr.TrimEnd());
                }

                if (outcome.Interruption is { } trouble)
                {
                    result.Append("— " + trouble + " —");
                    result.Message = trouble;
                    result.Status = OperationStatus.Failed;
                    return;
                }

                // ls не нашёл путь или не пустили — листинга нет: причина его же словами
                if (outcome.ExitCode is int code && code != 0)
                {
                    result.Message = outcome.StdErr.Trim().Split('\n')[0].Trim() is { Length: > 0 } why ? why : $"ls: код {code}";
                    result.Status = OperationStatus.Failed;
                    return;
                }

                result.Message = string.Empty;
                result.Status = OperationStatus.Success;
            },
            settings.MaxParallel,
            concurrent: true);

        // автокопия — когда закончился последний из шедших рядом
        if (!CommandBatch.IsRunning)
        {
            AppState.Current.NotifyFinished(CommandBatch);
        }

        return true;
    }

    // ── статус на ярлыке вкладки ───────────────────────────────────────────────

    // Шаги копирования словами. Коротко: на плашке ярлыка помещается около двадцати знаков.
    private const string StepConnecting = "подключение";
    private const string StepCheckingHashes = "сверка сумм";
    private const string StepCheckingDates = "сверка даты";
    private const string StepCopying = "копирование";
    private const string StepVerifying = "проверка копий";

    /// <summary>Сверка сумм идёт после всех шагов заливки — значит следующим номером.</summary>
    private const int StepVerifyingOrder = (int)UploadStep.Copying + 1;

    public void ShowStatus(string badge, string caption, WorkspaceStatus kind, string? tooltip = null)
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => ShowStatus(badge, caption, kind, tooltip)));
            return;
        }

        StatusBadge = badge;
        StatusCaption = caption;
        StatusTooltip = tooltip ?? $"{caption}: {badge}";
        Status = kind;

        // сам по себе статус не гаснет: его гасит окно, когда человек увидел вкладку
    }

    public void ClearStatus()
    {
        StatusBadge = string.Empty;
        StatusCaption = string.Empty;
        StatusTooltip = string.Empty;
        Status = WorkspaceStatus.None;
    }

    public WorkspaceState ToState() => new()
    {
        Id = Id,
        Title = Title,
        AfterCopyTabId = _afterCopyTabId,
        AfterCopyEnabled = _afterCopyEnabled,
        OwnerCopyTabId = OwnerCopyTabId,
        CopyWindowName = CopyWindows[0].Name,
        CopyWindowCollapsed = CopyWindows[0].IsCollapsed,
        ShowWindowNames = _showWindowNames,
        SeparateServers = _separateServers,
        Downloads = CopyWindows[0].Downloads.Select(d => d.ToSaved()).ToList(),
        LocalFolder = CopyWindows[0].LocalFolder,
        Explorer = Explorer.Clone(),
        IsMain = IsMain,
        IsArchived = IsArchived,
        KeepOutputLog = KeepOutputLog,
        AutoCopy = _autoCopy,
        LogAsFile = _logAsFile,
        CopyPass = _copyPass,
        SelectedServerIds = ServerIdsOf(CopyWindows[0]).ToList(),
        CommandServerIds = CommandServers.Where(c => c.IsSelected).Select(c => c.Server.Id).ToList(),
        CopyList = SavedItems(CopyWindows[0]),
        RemotePath = CopyWindows[0].RemotePath,
        LastLocalFolder = LastLocalFolder,
        OverwriteFiles = OptionsOf(CopyWindows[0]).Overwrite,
        PreserveTimestamps = OptionsOf(CopyWindows[0]).PreserveTimestamps,
        VerifyChecksums = OptionsOf(CopyWindows[0]).VerifyChecksums,
        ReplaceWithoutAsking = ReplaceWithoutAsking,
        ExcludeMasks = OptionsOf(CopyWindows[0]).ExcludeMasks,
        UseGlobalExcludes = OptionsOf(CopyWindows[0]).UseGlobalExcludes,
        MatchByDate = OptionsOf(CopyWindows[0]).MatchByDate,
        MatchByHash = OptionsOf(CopyWindows[0]).MatchByHash,
        ExtraCopyWindows = CopyWindows.Count > 1 ? CopyWindows.Skip(1).Select(SaveCopyWindow).ToList() : null,
        FocusedCopyWindow = CopyWindows.IndexOf(_focusedWindow),
        CommandText = CommandText,
        UseSudo = UseSudo,
        Commands = SaveSteps(),
        PaneSetId = PaneOf(CopyWindows[0]).SetId,
        PaneAllSets = PaneOf(CopyWindows[0]).AllSets,
        PaneScopes = PaneOf(CopyWindows[0]).Scopes,
        PaneShowIp = _paneShowIp,
        PaneShowUser = _paneShowUser,
        LockedServerIds = _lockedIds?.ToList(),
        JournalWhole = JournalWhole,
        BookGroup = BookGroup,
        BookScroll = BookScroll,
        BookHistory = BookHistory,
        CommandHistory = _commandHistory.Count > 0 ? _commandHistory.ToList() : null,
        SizeColumnsShown = _sizeColumnsShown?.ToList(),
    };

    /// <summary>Содержимое вкладки — для отмены действий.</summary>
    public TabContent CaptureContent() => new(
        Title,
        CopyWindows.Select(SaveCopyWindow).ToList(),
        CopyWindows.IndexOf(_focusedWindow),
        SaveSteps(),
        _showWindowNames);

    /// <summary>Отмена или возврат: окна те же, где можно, содержимое — из снимка.</summary>
    public void ApplyContent(TabContent content)
    {
        // галочки — из снимка, память «Незалитых» — нынешняя: отмена не должна забывать, что уже скопировано
        var memories = new Dictionary<string, RowMemory>(StringComparer.Ordinal);

        foreach (var window in CopyWindows)
        {
            foreach (var item in window.Items)
            {
                memories[item.FullPath] = item.Memory;
            }

            foreach (var download in window.Downloads)
            {
                memories[download.ServerId + "|" + download.Path] = download.Memory;
            }
        }

        // серверы и набор окна снимок не трогает: они остаются, только переходят к окну в фокусе
        var before = _focusedWindow;
        var existing = CopyWindows.Count;
        _restoring = true;

        try
        {
            while (CopyWindows.Count > content.Windows.Count)
            {
                var gone = CopyWindows[^1];

                if (ReferenceEquals(gone, _focusedWindow))
                {
                    _focusedWindow = CopyWindows[0];
                }

                gone.Items.Clear();
                gone.Items.CollectionChanged -= OnItemsChanged;
                gone.Downloads.Clear();
                gone.Downloads.CollectionChanged -= OnDownloadsChanged;
                gone.Detach();
                CopyWindows.Remove(gone);
            }

            while (CopyWindows.Count < content.Windows.Count)
            {
                CopyWindows.Add(NewCopyWindow(string.Empty));
            }

            for (var i = 0; i < content.Windows.Count; i++)
            {
                var (window, saved) = (CopyWindows[i], content.Windows[i]);

                window.RestorePath(saved.RemotePath ?? string.Empty);
                window.RestoreFolder(saved.LocalFolder ?? string.Empty);
                window.RestoreName(saved.Name ?? string.Empty);
                window.Options = new CopyOptions(
                    saved.OverwriteFiles,
                    saved.PreserveTimestamps,
                    saved.VerifyChecksums,
                    saved.MatchByDate,
                    saved.MatchByHash,
                    saved.ExcludeMasks ?? string.Empty,
                    saved.UseGlobalExcludes);

                // вернувшееся окно — с серверами и набором, что были у него
                if (i >= existing)
                {
                    window.ServerIds = saved.ServerIds?.ToList();
                    window.Pane = PaneFrom(saved);
                }

                window.Items.Clear();
                RestoreItems(window, saved.CopyList);

                window.Downloads.Clear();

                foreach (var download in saved.Downloads ?? [])
                {
                    window.Downloads.Add(DownloadItem.From(download));
                }

                foreach (var item in window.Items)
                {
                    if (memories.TryGetValue(item.FullPath, out var memory))
                    {
                        item.Memory = memory;
                    }
                }

                foreach (var download in window.Downloads)
                {
                    if (memories.TryGetValue(download.ServerId + "|" + download.Path, out var memory))
                    {
                        download.Memory = memory;
                    }
                }
            }

            RenumberWindows();

            foreach (var window in CopyWindows)
            {
                window.IsFocused = false;
            }

            _focusedWindow = CopyWindows[Math.Clamp(content.Focused, 0, CopyWindows.Count - 1)];
            _focusedWindow.IsFocused = true;
            LoadCopyOptions(_focusedWindow.Options ?? CurrentCopyOptions);
            SwapChecks(before, _focusedWindow);

            // окна команды пересобираются, только если они менялись: иначе курсор и прокрутка сбросились бы зря
            if (System.Text.Json.JsonSerializer.Serialize(SaveSteps()) != System.Text.Json.JsonSerializer.Serialize(content.Steps))
            {
                SetSteps(content.Steps);
            }
        }
        finally
        {
            _restoring = false;
        }

        // кнопка «Название» — как было в снимке; окна услышат сами
        if (_showWindowNames != content.ShowNames)
        {
            _showWindowNames = content.ShowNames;
            Raise(nameof(ShowWindowNames));
        }

        RaiseCopyOptions();
        RaiseCopyInfo();
        RaiseDownloadInfo();
        _ = RefreshItemsInfoAsync();
        Save();
    }

    private static List<SavedTransferItem> SavedItems(CopyWindow window) =>
        window.Items.Select(i => new SavedTransferItem { Path = i.FullPath, IsSelected = i.IsSelected, Smart = i.Memory.Copy() }).ToList();

    private SavedCopyWindow SaveCopyWindow(CopyWindow window)
    {
        var options = OptionsOf(window);
        var pane = IsDownloadTab ? null : StoredPaneOf(window);

        return new SavedCopyWindow
        {
            CopyList = SavedItems(window),
            RemotePath = window.RemotePath,
            ServerIds = IsDownloadTab ? null : StoredIdsOf(window),
            PaneSetId = pane?.SetId,
            PaneAllSets = pane?.AllSets ?? false,
            PaneScopes = pane?.Scopes,
            Name = window.Name,
            Collapsed = window.IsCollapsed,
            Downloads = window.Downloads.Count > 0 ? window.Downloads.Select(d => d.ToSaved()).ToList() : null,
            LocalFolder = window.LocalFolder,
            OverwriteFiles = options.Overwrite,
            PreserveTimestamps = options.PreserveTimestamps,
            VerifyChecksums = options.VerifyChecksums,
            MatchByDate = options.MatchByDate,
            MatchByHash = options.MatchByHash,
            ExcludeMasks = options.ExcludeMasks,
            UseGlobalExcludes = options.UseGlobalExcludes,
        };
    }

    /// <summary>Строки списка из файла. Пропавшие локальные пути отбрасываются. Вернёт, сколько пропало.</summary>
    private int RestoreItems(CopyWindow window, IEnumerable<SavedTransferItem> saved)
    {
        var missing = 0;

        foreach (var item in saved)
        {
            if (File.Exists(item.Path) || Directory.Exists(item.Path))
            {
                window.Items.Add(new TransferItem(item.Path) { IsSelected = item.IsSelected, Memory = item.Smart?.Copy() ?? new() });
            }
            else
            {
                missing++;
                AppLog.Warn(OnTab($"Пропал объект из списка копирования: {item.Path}"));
            }
        }

        return missing;
    }

    /// <summary>Восстанавливает вкладку из файла. Пропавшие локальные пути отбрасываются.</summary>
    public void Restore(WorkspaceState state)
    {
        CopyWindows[0].RestorePath(string.IsNullOrWhiteSpace(state.RemotePath) ? "/" : state.RemotePath);
        CopyWindows[0].RestoreName(state.CopyWindowName ?? string.Empty);
        CopyWindows[0].IsCollapsed = state.CopyWindowCollapsed;
        _showWindowNames = state.ShowWindowNames;
        _separateServers = state.SeparateServers;
        _lastLocalFolder = state.LastLocalFolder ?? string.Empty;
        _overwrite = state.OverwriteFiles;
        _preserveTimestamps = state.PreserveTimestamps;
        _verifyChecksums = state.VerifyChecksums;
        _replaceWithoutAsking = state.ReplaceWithoutAsking;
        _excludeMasks = state.ExcludeMasks ?? string.Empty;
        _useGlobalExcludes = state.UseGlobalExcludes;
        _matchByDate = state.MatchByDate;
        _matchByHash = state.MatchByHash;
        _afterCopyTabId = state.AfterCopyTabId ?? string.Empty;
        _afterCopyEnabled = state.AfterCopyEnabled && _afterCopyTabId.Length > 0;
        _isArchived = state.IsArchived && !IsMain;
        _keepOutputLog = state.KeepOutputLog ?? LegacyKeepLog;
        _autoCopy = state.AutoCopy ?? false;
        _logAsFile = state.LogAsFile ?? false;
        _copyPass = state.CopyPass ?? false;
        OwnerCopyTabId = state.OwnerCopyTabId ?? string.Empty;

        CopyWindows[0].RestoreFolder(state.LocalFolder ?? string.Empty);
        Explorer = state.Explorer?.Clone() ?? new ExplorerTabState();

        _restoring = true;

        try
        {
            CopyWindows[0].Downloads.Clear();

            foreach (var saved in state.Downloads ?? [])
            {
                CopyWindows[0].Downloads.Add(DownloadItem.From(saved));
            }

            SetSteps(state.Commands is { Count: > 0 }
                ? state.Commands
                : [new SavedCommandStep { Text = state.CommandText ?? string.Empty, UseSudo = state.UseSudo }]);
        }
        finally
        {
            _restoring = false;
        }
        _paneSetId = state.PaneSetId ?? string.Empty;
        _paneAllSets = state.PaneAllSets;
        _paneShowIp = state.PaneShowIp ?? false;
        _paneShowUser = state.PaneShowUser ?? false;
        JournalWhole = state.JournalWhole;
        BookGroup = state.BookGroup;
        BookScroll = state.BookScroll;
        BookHistory = state.BookHistory;
        _commandHistory.Clear();

        foreach (var text in state.CommandHistory ?? [])
        {
            _commandHistory.Add(text);
        }

        _sizeColumnsShown = state.SizeColumnsShown?.ToList();
        _lockedIds = state.LockedServerIds?.ToHashSet(StringComparer.Ordinal);

        // серверы к этому моменту уже могли прийти — замок сверяется с ними и с открытым набором здесь же
        SyncLock();
        _scopes.Clear();

        foreach (var scope in state.PaneScopes ?? [])
        {
            _scopes[scope.Key] = scope;
        }

        // 2.0.8: один свитч на все наборы вкладки — его состояние и память достаются открытому набору
        if (state.PaneScopes is null && state.LegacySingleSelect is { } single)
        {
            var key = _paneAllSets ? AllScope : _paneSetId;
            _scopes[key] = new PaneScopeState
            {
                Key = key,
                SingleSelect = single,
                MultiServerIds = state.LegacyMultiServerIds,
                SinglePickId = state.LegacySinglePickId,
            };
        }

        var missing = RestoreItems(CopyWindows[0], state.CopyList);

        foreach (var saved in state.ExtraCopyWindows ?? [])
        {
            var window = NewCopyWindow(saved.RemotePath ?? string.Empty);
            window.Options = new CopyOptions(
                saved.OverwriteFiles,
                saved.PreserveTimestamps,
                saved.VerifyChecksums,
                saved.MatchByDate,
                saved.MatchByHash,
                saved.ExcludeMasks ?? string.Empty,
                saved.UseGlobalExcludes);
            window.RestoreFolder(saved.LocalFolder ?? string.Empty);
            window.RestoreName(saved.Name ?? string.Empty);
            window.IsCollapsed = saved.Collapsed;

            // файл прежней версии: серверы у окон были общие — как у окна #1
            window.ServerIds = saved.ServerIds?.ToList() ?? state.SelectedServerIds?.ToList();
            window.Pane = PaneFrom(saved) ?? PaneNow();
            CopyWindows.Add(window);
            missing += RestoreItems(window, saved.CopyList);

            _restoring = true;

            try
            {
                foreach (var download in saved.Downloads ?? [])
                {
                    window.Downloads.Add(DownloadItem.From(download));
                }
            }
            finally
            {
                _restoring = false;
            }
        }

        RenumberWindows();

        // «Открываться там, где ушёл»: панель и параметры — на том окне, что было в фокусе
        if (state.FocusedCopyWindow > 0 && state.FocusedCopyWindow < CopyWindows.Count)
        {
            var window = CopyWindows[state.FocusedCopyWindow];
            var from = _focusedWindow;
            _focusedWindow.Options = CurrentCopyOptions;
            _focusedWindow.IsFocused = false;
            _focusedWindow = window;
            window.IsFocused = true;
            LoadCopyOptions(window.Options ?? CurrentCopyOptions);
            SwapChecks(from, window, raise: false);
        }

        if (AllItems.Any() || missing > 0)
        {
            AppLog.Debug($"Список копирования восстановлен: {AllItems.Count()} объект(ов)"
                + (missing > 0 ? $", не найдено {missing}" : string.Empty), Title);
        }

        RaiseMany(
            nameof(RemotePath),
            nameof(Items),
            nameof(Downloads),
            nameof(LocalFolder),
            nameof(DownloadInfo),
            nameof(AllDownloadsSelected),
            nameof(ShowWindowNames),
            nameof(OverwriteFiles),
            nameof(PreserveTimestamps),
            nameof(VerifyChecksums),
            nameof(ReplaceWithoutAsking),
            nameof(ExcludeMasks),
            nameof(HasExcludes),
            nameof(HasGlobalExcludes),
            nameof(UseGlobalExcludes),
            nameof(MatchByDate),
            nameof(MatchByHash),
            nameof(SendEverything),
            nameof(SendMissingOnly),
            nameof(SendByDate),
            nameof(SendByHash),
            nameof(CommandText),
            nameof(CommandSizeInfo),
            nameof(UseSudo));
    }

    private string Operation(string name) => OnTab(name);

    /// <summary>Первый блок вывода операции: с чем она запущена. Журнал читает это из лога записи.</summary>
    private const string BlockParams = "── параметры ──";

    /// <summary>Условия копирования словами — в блок параметров и в файл дня.</summary>
    private static string UploadTerms(UploadJob job) =>
        (job.Options.MatchByHash ? ", только отличающиеся по контрольной сумме" : string.Empty)
        + (job.Options.MatchByDate && !job.Options.MatchByHash ? ", только отличающиеся по дате" : string.Empty)
        + (job.Verify ? ", с проверкой копий" : string.Empty);

    /// <summary>Запись журнала с подписью вкладки: столбец «Где» — только для серверов.</summary>
    private string OnTab(string text) => $"{text.TrimEnd('.')} · вкладка «{Title}»";

    /// <summary>
    /// Помечает строки результатов названием вкладки — нужно для сводной таблицы.
    /// Когда вкладка одна, метка только мешает, поэтому её нет.
    /// </summary>
    private void SetScope()
    {
        var tabs = AppState.Current.TabsOf(this);
        var scope = tabs.Count > 1 && tabs.Contains(this) ? Title : null;

        CopyBatch.Scope = scope;
        CommandBatch.Scope = scope;
        CheckBatch.Scope = scope;

        var section = AppState.Current.SectionOfTab(this);
        CopyBatch.Section = section;
        CommandBatch.Section = section;
        // проверку связи зовут и со страницы «Серверы» — запись туда, откуда позвали
        CheckBatch.Section = AppState.Current.Section;
    }

    /// <summary>Прогон, в который ложатся строки разовых действий: у командной вкладки — команд, у прочих — копирования.</summary>
    private OperationBatch ActionBatch => AppState.Current.CommandTabs.Contains(this) ? CommandBatch : CopyBatch;

    /// <summary>Разовое действие — строкой в терминал этой вкладки; терминал переключается на этот прогон.</summary>
    internal void AddActionRow(ServerEntry where, LogLevel level, string summary, IReadOnlyList<string> lines)
    {
        SetScope();
        var batch = ActionBatch;
        batch.AddAction(where, level, summary, lines);
        ShowResults(batch);
    }

    /// <summary>Терминал внизу окна показывает ту операцию, которую запустили последней.</summary>
    private void ShowResults(OperationBatch batch)
    {
        if (!ReferenceEquals(_resultsBatch, batch))
        {
            _resultsBatch = batch;
            Raise(nameof(ResultsBatch));
        }
    }

    /// <summary>
    /// Итог операции на ярлыке. На плашке — одна строка про саму задачу («5 из 5»),
    /// без числа серверов. Вторая строка только про ошибки, и её нет, если ошибок нет:
    /// что именно за ошибка — в терминале, там же фактические числа по каждому серверу.
    /// </summary>
    /// <param name="what">Название операции — идёт в подсказку.</param>
    /// <param name="badge">Единственная строка плашки: что сделано.</param>
    /// <param name="note">Хвост подсказки: подробность, которую стоит пояснить.</param>
    private void FinishStatus(OperationBatch batch, string what, string badge, string? note = null)
    {
        var total = batch.RunTotal;
        var ok = batch.RunSuccess;
        var failed = batch.RunFailed;

        if (total == 0)
        {
            ClearStatus();
            return;
        }

        // остановленное человеком — не ошибка: рамка оранжевая, как у плашки об этом
        var stopped = batch.RunStopped;
        var errors = Math.Max(0, failed - stopped);

        var kind = errors > 0
            ? ok == 0 ? WorkspaceStatus.Failed : WorkspaceStatus.Partial
            : stopped > 0 ? WorkspaceStatus.Warning : WorkspaceStatus.Success;

        var tooltip = $"{what}: {badge}"
            + (string.IsNullOrEmpty(note) ? string.Empty : $" · {note}")
            + (CopyWindows.Count > 1 ? $" · строк отработало {ok} из {total}" : $" · серверов отработало {ok} из {total}")
            + (errors > 0 ? $", с ошибкой {errors} — что за ошибка, видно в журнале" : string.Empty)
            + (stopped > 0 ? $", остановлено {stopped}" : string.Empty);

        ShowStatus(badge, errors > 0 ? $"Ошибок {errors}" : string.Empty, kind, tooltip);
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var item in e.NewItems?.OfType<TransferItem>() ?? [])
        {
            item.PropertyChanged += OnItemChanged;
        }

        foreach (var item in e.OldItems?.OfType<TransferItem>() ?? [])
        {
            item.PropertyChanged -= OnItemChanged;
        }

        foreach (var window in CopyWindows)
        {
            window.RaiseItems();
        }

        SyncWatchers();
        RaiseCopyInfo();

        if (!_restoring)
        {
            Save();
        }
    }

    /// <summary>
    /// Наблюдатели ровно по строкам списка: у новых заводятся, у убранных снимаются.
    /// Сверка с самим списком, а не с событием — очистка списка приходит без перечня строк.
    /// </summary>
    private void SyncWatchers()
    {
        foreach (var gone in _watchers.Keys.Where(k => !AllItems.Contains(k)).ToList())
        {
            _watchers[gone].Dispose();
            _watchers.Remove(gone);
        }

        foreach (var item in AllItems.Where(i => !_watchers.ContainsKey(i)))
        {
            _watchers[item] = new ItemWatcher(item, OnItemChangedOnDisk, OnWatchLost);
        }
    }

    /// <summary>Вкладку закрыли — следить за её файлами больше незачем.</summary>
    /// <summary>Вкладку вернули: снова следить за файлами списка.</summary>
    public void ResumeWatching() => SyncWatchers();

    public void StopWatching()
    {
        foreach (var watcher in _watchers.Values)
        {
            watcher.Dispose();
        }

        _watchers.Clear();
    }

    /// <summary>
    /// Файл или папка строки изменились на диске. Перечитываем только эту строку,
    /// а посчитанные суммы её файлов выбрасываем — они больше не про это содержимое.
    /// Вызывается из потока таймера наблюдателя.
    /// </summary>
    private void OnItemChangedOnDisk(TransferItem item)
    {
        var prefix = item.FullPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        foreach (var key in _localHashes.Keys)
        {
            // ключ — путь, за ним через перевод строки размер и дата (SshService.HashKey)
            if (string.Equals(key.Split('\n')[0], item.FullPath, StringComparison.OrdinalIgnoreCase)
                || key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                _localHashes.TryRemove(key, out _);
            }
        }

        try
        {
            item.Refresh(CopyWindows.FirstOrDefault(w => w.Items.Contains(item)) is { } window ? ExcludesOf(window) : null);
        }
        catch (Exception)
        {
            // строку перечитают при возврате в окно
        }

        if (!item.ExistsOnDisk)
        {
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => DropMissing([item])));
        }

        Application.Current?.Dispatcher.BeginInvoke(new Action(() => _ = FollowUnsentAsync()));
    }

    /// <summary>Сколько строка ждёт свой пропавший файл: архив удалили перед пересборкой — через секунды он снова на месте.</summary>
    private static readonly TimeSpan MissingGrace = TimeSpan.FromMinutes(1);

    /// <summary>Строки, чьих файлов сейчас нет на диске, и с какого времени.</summary>
    private readonly Dictionary<TransferItem, DateTime> _missingSince = new();

    private System.Windows.Threading.DispatcherTimer? _missingTimer;

    /// <summary>
    /// Строки, чьих файлов и папок на диске больше нет, уходят из списка не сразу, а через минуту:
    /// файл, удалённый перед пересборкой, появляется снова, и строка остаётся сама. Не появился —
    /// строка убирается: копировать нечего, а висящая с прочерками только путает.
    /// </summary>
    private void DropMissing(IEnumerable<TransferItem> candidates)
    {
        var now = DateTime.UtcNow;

        foreach (var item in candidates.Where(i => !i.ExistsOnDisk && AllItems.Contains(i)))
        {
            _missingSince.TryAdd(item, now);
        }

        if (_missingSince.Count > 0 && _missingTimer is null)
        {
            _missingTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _missingTimer.Tick += (_, _) => CheckMissing();
            _missingTimer.Start();
        }
    }

    private void CheckMissing()
    {
        var now = DateTime.UtcNow;
        var back = false;

        foreach (var item in _missingSince.Keys.ToList())
        {
            if (!AllItems.Contains(item))
            {
                _missingSince.Remove(item);
            }
            else if (item.ExistsOnDisk)
            {
                // файл вернулся: за удалённой папкой наблюдатель умер вместе с ней — заводим заново
                _missingSince.Remove(item);
                back = true;

                if (_watchers.Remove(item, out var dead))
                {
                    dead.Dispose();
                    _watchers[item] = new ItemWatcher(item, OnItemChangedOnDisk, OnWatchLost);
                }
            }
        }

        var gone = _missingSince.Where(p => now - p.Value >= MissingGrace).Select(p => p.Key).ToList();

        foreach (var item in gone)
        {
            _missingSince.Remove(item);
        }

        if (_missingSince.Count == 0 && _missingTimer is not null)
        {
            _missingTimer.Stop();
            _missingTimer = null;
        }

        if (back)
        {
            _ = RefreshItemsInfoAsync();
        }

        if (gone.Count == 0)
        {
            return;
        }

        RemoveItems(gone);

        var names = string.Join(", ", gone.Take(5).Select(i => i.Name))
            + (gone.Count > 5 ? $" и ещё {gone.Count - 5}" : string.Empty);

        AppLog.Info(OnTab($"Убрано из списка — на диске больше нет: {names}"));
    }

    /// <summary>Windows не успела передать все изменения — перечитываем весь список.</summary>
    private void OnWatchLost()
    {
        _localHashes.Clear();
        Application.Current?.Dispatcher.BeginInvoke(new Action(() => _ = RefreshItemsInfoAsync()));
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TransferItem.IsSelected))
        {
            foreach (var window in CopyWindows)
            {
                window.RaiseItems();
            }

            RaiseCopyInfo();
            Save();
        }
    }

    private void OnServerCheckChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ServerCheck.IsSelected))
        {
            if (_reverting || _swappingChecks)
            {
                return;
            }

            // под замком отметки не меняются ничем: ни щелчком, ни переходом, ни «Отметить все».
            // У окон копирования серверы разные, и замок держит их все — возвращается любая правка
            if (_lockedIds is not null && sender is ServerCheck check && LockList.Contains(check))
            {
                _reverting = true;

                try
                {
                    check.IsSelected = !check.IsSelected;
                }
                finally
                {
                    _reverting = false;
                }

                BlockedByLock();
                return;
            }

            // одиночный выбор: отметили сервер в панели — остальные в наборе снимаются
            if (sender is ServerCheck { IsSelected: true } picked
                && !_keepingOne
                && IsPaneSingleSelect
                && PaneChecks.Contains(picked))
            {
                KeepOnly(picked);
            }

            RaiseSelectionInfo();
            Save();
        }
    }

    private bool _keepingOne;

    // ── замок: вкладка закреплена за своими серверами ─────────────────────────────

    /// <summary>
    /// Серверы, за которыми закреплена вкладка; null — замка нет. Память своей вкладки:
    /// новая вкладка замок не наследует.
    /// </summary>
    private HashSet<string>? _lockedIds;

    /// <summary>Страж возвращает отметку: её же сигнал второй раз не разбирается.</summary>
    private bool _reverting;

    private DateTime _lockHintAt;

    /// <summary>
    /// Вкладка под замком: остальные серверы и наборы приглушены, сменить сервер нельзя. На «Копировании»
    /// и «Скачивании» заперто и всё остальное, кроме галочек у строк, с которыми пойдёт операция.
    /// </summary>
    public bool IsPaneLocked => _lockedIds is not null;

    public bool IsNotPaneLocked => _lockedIds is null;

    /// <summary>В «Командах» и «Проводнике» замок держит только сервер, на содержимое вкладки не влияет.</summary>
    private bool LockGuardsContent => !IsExplorerTab && !AppState.Current.CommandTabs.Contains(this);

    /// <summary>Замок запер содержимое вкладки — закрыть её тоже нельзя, иначе пропадёт всё запертое.</summary>
    public bool IsContentLocked => IsPaneLocked && LockGuardsContent;

    /// <summary>Подсказка у открытого замка: что он запрёт на этой вкладке.</summary>
    public string LockTip => LockGuardsContent
        ? "Закрепить вкладку за этим сервером: менять можно будет только галочки у строк"
        : IsExplorerTab
            ? "Закрепить вкладку за этим сервером и открытыми папками обеих панелей: внутрь них можно, выше — только сняв замок"
            : "Закрепить вкладку за этим сервером: сменить его можно будет, только сняв замок";

    /// <summary>«Проводник» под замком: папка сервера, выше которой правая панель не поднимается; пусто — папка не закреплена.</summary>
    public string ExplorerLockedPath => IsPaneLocked && IsExplorerTab ? Explorer.LockedRemotePath : string.Empty;

    /// <summary>«Проводник» под замком: папка компьютера, выше которой левая панель не поднимается; null — не закреплена.</summary>
    public string? ExplorerLockedLocalPath => IsPaneLocked && IsExplorerTab ? Explorer.LockedLocalPath : null;

    /// <summary>
    /// Замок включили, пока папка сервера не открылась, или он стоит с прошлой версии: закрепляется та папка,
    /// где панель открылась.
    /// </summary>
    public void AdoptExplorerLock()
    {
        if (IsPaneLocked && IsExplorerTab && Explorer.LockedRemotePath.Length == 0 && Explorer.Remote.Path.Length > 0
            && Explorer.RemoteServerId == ExplorerServer?.Id)
        {
            Explorer.LockedRemotePath = Explorer.Remote.Path;
            RaiseMany(nameof(ExplorerLockedPath));
        }
    }

    /// <summary>Отметки, которые держит замок: у командной вкладки — команды, у остальных — копирования.</summary>
    private ObservableCollection<ServerCheck> LockList =>
        AppState.Current.CommandTabs.Contains(this) ? CommandServers : CopyServers;

    /// <summary>
    /// Замок в строке сервера. Закрыть: сервер отмечается (если ещё не отмечен), и вкладка
    /// закрепляется за отмеченными. Открыть — тем же замком.
    /// </summary>
    public void TogglePaneLock(ServerCheck clicked)
    {
        if (_lockedIds is not null)
        {
            _lockedIds = null;
            Explorer.LockedRemotePath = string.Empty;
            Explorer.LockedLocalPath = null;
            ApplyLock();
            AppLog.Hint($"Вкладка «{Title}»: замок снят, сервер снова можно менять.");
            Save();
            return;
        }

        if (!clicked.IsSelected)
        {
            clicked.IsSelected = true;
        }

        // только отмеченные в открытом наборе: отметки, которые другие наборы помнят за собой, в замок не идут —
        // иначе их серверы считались бы закреплёнными и не гасли в списке у подписи сервера
        var picked = InActiveSet(LockList).Where(c => c.IsSelected).Select(c => c.Server).ToList();

        // у окон копирования серверы свои — замок держит серверы всех окон сразу
        if (ReferenceEquals(LockList, CopyServers) && WindowServers)
        {
            foreach (var window in CopyWindows)
            {
                picked.AddRange(TargetsOf(window).Where(s => !picked.Contains(s)));
            }
        }

        _lockedIds = picked.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);

        // «Проводник»: замок держит и открытые папки обеих панелей — внутрь можно, выше нельзя.
        // Сервер только что сменили — его папка закрепится при открытии
        if (IsExplorerTab)
        {
            Explorer.LockedRemotePath = Explorer.RemoteServerId == ExplorerServer?.Id ? Explorer.Remote.Path : string.Empty;
            Explorer.LockedLocalPath = Explorer.Local.Path;
        }

        ApplyLock();
        AppLog.Hint($"Вкладка «{Title}» закреплена: {string.Join(", ", picked.Select(s => s.PaneLabel))}. "
            + (LockGuardsContent
                ? "Менять можно только галочки у строк, для остального требуется снять замок."
                : IsExplorerTab ? "В открытые папки можно заходить глубже, выше них и на другой сервер — сняв замок." : "Сменить — сняв замок."));
        Save();
    }

    /// <summary>Под замком действие, меняющее серверы вкладки, не выполняется — и об этом плашка.</summary>
    public bool BlockedByLock()
    {
        if (_lockedIds is null)
        {
            return false;
        }

        // одно действие трогает много отметок — плашка одна. Промежуток короткий:
        // щелчок закрывает плашку, и следующий щелчок должен показать её снова
        if (DateTime.UtcNow - _lockHintAt > TimeSpan.FromMilliseconds(300))
        {
            _lockHintAt = DateTime.UtcNow;
            AppLog.Hint($"Вкладка «{Title}» закреплена за своим сервером — требуется снять замок.", LogLevel.Warning);
        }

        return true;
    }

    /// <summary>
    /// Строки панели узнают, кто закреплён, а кто приглушён. В обоих списках отметок: при загрузке
    /// вкладка может ещё не знать, командная она или нет.
    /// </summary>
    private void ApplyLock()
    {
        foreach (var check in CopyServers.Concat(CommandServers))
        {
            var pinned = _lockedIds?.Contains(check.Server.Id) == true;
            check.IsLocked = pinned;
            check.IsMuted = _lockedIds is not null && !pinned;
        }

        RaiseMany(nameof(IsPaneLocked), nameof(IsNotPaneLocked), nameof(IsContentLocked), nameof(ExplorerLockedPath), nameof(ExplorerLockedLocalPath));
    }

    /// <summary>
    /// Список серверов поменялся: новые приходят приглушёнными, удалённые уходят из замка.
    /// Не осталось ни одного закреплённого — замок снимается, иначе его нечем было бы открыть.
    /// </summary>
    private void SyncLock()
    {
        if (_lockedIds is null)
        {
            return;
        }

        // серверы ещё не загружены — снимать нечего
        if (CopyServers.Count == 0)
        {
            ApplyLock();
            return;
        }

        var before = _lockedIds.Count;
        var present = CopyServers.Select(c => c.Server.Id).ToHashSet(StringComparer.Ordinal);
        _lockedIds.IntersectWith(present);

        // до 3.1.0 в замок попадали и отметки, которые помнят другие наборы: под замком набор не меняется,
        // так что закреплённые — только из открытого
        if (!_paneAllSets && PaneSet is { } set)
        {
            _lockedIds.RemoveWhere(id => !set.Servers.Any(s => s.Id == id));
        }

        if (_lockedIds.Count == 0)
        {
            _lockedIds = null;
            AppLog.Hint($"Вкладка «{Title}»: закреплённого сервера больше нет — замок снят.");
            Save();
        }
        else if (_lockedIds.Count != before)
        {
            Save();
        }

        ApplyLock();
    }

    /// <summary>Одиночный выбор действует на «Команде» и «Копировании»; на «Серверах» галочки обычные.</summary>
    public bool IsPaneSingleSelect =>
        AppState.Current.Section == AppSection.Explorer
        || (SingleSelect && AppState.Current.Section is AppSection.Command or AppSection.Files or AppSection.Download);

    /// <summary>Отметки, которые сейчас в панели: команды на «Команде», иначе копирования.</summary>
    private ObservableCollection<ServerCheck> PaneChecks =>
        AppState.Current.Section == AppSection.Command ? CommandServers : CopyServers;

    private void KeepOnly(ServerCheck keep)
    {
        _keepingOne = true;

        try
        {
            foreach (var check in InActiveSet(PaneChecks))
            {
                if (!ReferenceEquals(check, keep) && check.IsSelected)
                {
                    check.IsSelected = false;
                }
            }
        }
        finally
        {
            _keepingOne = false;
        }
    }

    /// <summary>
    /// Включили одиночный выбор, а в панели отмечено несколько серверов (для команды или
    /// для копирования — смотря какой раздел открыт): остаётся первый по списку,
    /// остальные снимаются — и об этом пишется в журнал.
    /// </summary>
    public void KeepOneServer()
    {
        var picked = InActiveSet(PaneChecks).Where(c => c.IsSelected).ToList();

        if (picked.Count <= 1)
        {
            return;
        }

        KeepOnly(picked[0]);
        AppLog.Hint($"Одиночный выбор: оставлен один сервер — {picked[0].Server.PaneLabel}.");
    }

    private void RaiseCopyInfo() => RaiseMany(
        nameof(UnsentOn),
        nameof(TargetsInfo),
        nameof(DownloadInfo),
        nameof(AllItemsSelected),
        nameof(AllWindowsItemsSelected),
        nameof(TabCounts),
        nameof(TabTooltip));

    private void RaiseSelectionInfo()
    {
        RaiseSelectionCounts();
        RaiseNotices();
    }

    private void RaiseSelectionCounts() => RaiseMany(
        nameof(SelectionInfo),
        nameof(PaneAllChecked),
        nameof(PaneTitle),
        nameof(PaneEmptyHintVisibility),
        nameof(TargetsInfo),
        nameof(DownloadInfo),
        nameof(CommandTargetsInfo),
        nameof(AllServersSelected),
        nameof(ExplorerServer),
        nameof(TabCounts),
        nameof(TabTooltip));

    private static void Save() => AppState.Current.SaveWorkspaces();

    private static string Preview(string command)
    {
        var first = command.Replace("\r", string.Empty).Split('\n')[0].Trim();
        return first.Length > 120 ? first[..117] + "…" : first;
    }

    private static bool? TriState(int total, int selected)
    {
        if (total == 0 || selected == 0)
        {
            return false;
        }

        return selected == total ? true : null;
    }

}
