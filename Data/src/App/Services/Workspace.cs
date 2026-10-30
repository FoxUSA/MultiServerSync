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
    private string _remotePath = "/";
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

    /// <summary>Слежение за файлами списка: по одному наблюдателю на строку.</summary>
    private readonly Dictionary<TransferItem, ItemWatcher> _watchers = new();

    public Workspace(string id, string title, bool isMain)
    {
        Id = id;
        IsMain = isMain;
        _title = title;

        Items.CollectionChanged += OnItemsChanged;

        Steps.CollectionChanged += OnStepsChanged;
        Steps.Add(new CommandStep());

        Downloads.CollectionChanged += OnDownloadsChanged;

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
    private IReadOnlyList<ServerCheck> InActiveSet(IEnumerable<ServerCheck> set)
    {
        if (_paneAllSets || PaneSet is not { } active)
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

    public ObservableCollection<TransferItem> Items { get; } = new();

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

    /// <summary>Что показывает терминал внизу окна: результат последней запущенной операции.</summary>
    public OperationBatch ResultsBatch => _resultsBatch ?? CommandBatch;

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
        get => _remotePath;
        set { if (Set(ref _remotePath, value)) { Save(); } }
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
        set { if (Set(ref _overwrite, value)) { RaiseMode(); Save(); } }
    }

    public bool PreserveTimestamps
    {
        get => _preserveTimestamps;
        set { if (Set(ref _preserveTimestamps, value)) { Save(); } }
    }

    public bool VerifyChecksums
    {
        get => _verifyChecksums;
        set { if (Set(ref _verifyChecksums, value)) { Save(); } }
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
        set { if (Set(ref _excludeMasks, value ?? string.Empty)) { Raise(nameof(HasExcludes)); Save(); } }
    }

    /// <summary>Свои исключения заданы — значок кнопки на зелёной плашке: что-то из списка не поедет.</summary>
    public bool HasExcludes => ExcludeRules.Count(_excludeMasks) > 0;

    /// <summary>Глобальные исключения действуют и здесь — переключатель на вкладке «Глобально» окна исключений.</summary>
    public bool UseGlobalExcludes
    {
        get => _useGlobalExcludes;
        set { if (Set(ref _useGlobalExcludes, value)) { Raise(nameof(HasGlobalExcludes)); Save(); } }
    }

    /// <summary>Глобальные исключения заданы и здесь не выключены — без своих значок на фиолетовой плашке.</summary>
    public bool HasGlobalExcludes => _useGlobalExcludes && ExcludeRules.Count(AppState.Current.GlobalExcludeMasks) > 0;

    public void RaiseGlobalExcludes() => Raise(nameof(HasGlobalExcludes));

    /// <summary>Глобальные маски для захода — пусто, если на этой вкладке они выключены.</summary>
    private string GlobalExcludeMasks => _useGlobalExcludes ? AppState.Current.GlobalExcludeMasks : string.Empty;

    /// <summary>Заменять файлы с другой датой изменения — в любую сторону, а не только старее.</summary>
    public bool MatchByDate
    {
        get => _matchByDate;
        set { if (Set(ref _matchByDate, value)) { RaiseMode(); Save(); } }
    }

    /// <summary>Заменять файлы с другой контрольной суммой: решение по содержимому, а не по дате.</summary>
    public bool MatchByHash
    {
        get => _matchByHash;
        set { if (Set(ref _matchByHash, value)) { RaiseMode(); Save(); } }
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
        ? AppState.Current.Workspaces.FirstOrDefault(w => w.Id == OwnerCopyTabId)
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

    private string _localFolder = string.Empty;

    /// <summary>
    /// Что скачивать: файлы и папки, каждая строка со своего сервера — их выбирают обзором сервера.
    /// Качаются строки с галочкой; отметки серверов слева здесь не нужны.
    /// </summary>
    public ObservableCollection<DownloadItem> Downloads { get; } = new();

    private IEnumerable<DownloadItem> PickedDownloads => Downloads.Where(d => d.IsSelected);

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
        RaiseMany(nameof(DownloadInfo), nameof(AllDownloadsSelected), nameof(TabCounts), nameof(TabTooltip));

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

    /// <summary>Куда скачивать. С нескольких серверов — каждый в свою подпапку по имени сервера.</summary>
    public string LocalFolder
    {
        get => _localFolder;
        set
        {
            if (Set(ref _localFolder, value ?? string.Empty))
            {
                Raise(nameof(DownloadInfo));
                Save();
            }
        }
    }

    public string DownloadInfo
    {
        get
        {
            var picked = PickedDownloads.ToList();
            var servers = picked.Select(d => d.ServerId).Distinct().Count();
            var where = servers > 1 ? " · каждый сервер — в свою папку" : string.Empty;

            return $"Будет скачано отмеченных объектов: {picked.Count} из {Downloads.Count} · с серверов: {servers}{where}.";
        }
    }

    /// <summary>Добавить выбранное в обзоре сервера, без повторов. Вернёт, сколько добавилось.</summary>
    public int AddDownloads(ServerEntry server, IEnumerable<(string Path, bool IsDirectory, long Size, DateTime Modified)> picked)
    {
        var added = 0;

        foreach (var (raw, isDirectory, size, modified) in picked)
        {
            var path = SshService.NormalizeRemote(raw);

            if (path.Length <= 1 || Downloads.Any(d => d.ServerId == server.Id && d.Path == path))
            {
                continue;
            }

            Downloads.Add(new DownloadItem(server.Id, path, isDirectory, size, modified));
            added++;
        }

        return added;
    }

    /// <summary>
    /// Строке скачивания вписали другой путь на том же сервере: она встаёт на то же место заново,
    /// размер и дата — после проверки списка. Вернёт причину отказа или null.
    /// </summary>
    public string? ReplaceDownloadPath(DownloadItem item, string raw)
    {
        var path = SshService.NormalizeRemote(raw.Trim().Trim('"'));
        var index = Downloads.IndexOf(item);

        if (index < 0 || path == item.Path)
        {
            return null;
        }

        if (path.Length <= 1)
        {
            return "корень «/» целиком не скачивается — укажите папку";
        }

        if (Downloads.Any(d => !ReferenceEquals(d, item) && d.ServerId == item.ServerId && d.Path == path))
        {
            return "этот путь уже есть в списке";
        }

        Downloads[index] = new DownloadItem(item.ServerId, path, item.IsDirectory, -1, default) { IsSelected = item.IsSelected };
        _ = RefreshDownloadsAsync();
        return null;
    }

    private bool _checkingDownloads;

    /// <summary>
    /// Список скачивания — заново с серверов: размер, число файлов в папке, дата самого свежего
    /// файла, пропавшие пути. Одна команда на сервер; на время скачивания не трогаем.
    /// </summary>
    public async Task RefreshDownloadsAsync()
    {
        if (_checkingDownloads || CopyBatch.IsRunning || Downloads.Count == 0)
        {
            return;
        }

        _checkingDownloads = true;

        try
        {
            var settings = AppState.Current.Settings;

            await Task.WhenAll(Downloads
                .GroupBy(d => d.ServerId)
                .Select(g => (Server: AppState.Current.Servers.FirstOrDefault(s => s.Id == g.Key), Items: g.ToList()))
                .Where(g => g.Server is not null)
                .Select(g => CheckDownloadsAsync(g.Server!, g.Items, settings)));
        }
        finally
        {
            _checkingDownloads = false;
        }
    }

    private static async Task CheckDownloadsAsync(ServerEntry server, List<DownloadItem> items, AppSettings settings)
    {
        // строка ответа: вид|файлов|байт|дата|номер строки — по номеру, путь может содержать «|»
        var script = new StringBuilder();

        for (var i = 0; i < items.Count; i++)
        {
            var p = SshService.ShellQuote(items[i].Path);
            script.Append("p=").Append(p).Append("; if [ -d \"$p\" ]; then find \"$p\" -type f -printf '%s %T@\\n' 2>/dev/null")
                .Append(" | awk 'BEGIN{n=0;s=0;m=0} {n++; s+=$1; if ($2>m) m=$2} END{printf \"D|%d|%.0f|%d|").Append(i).Append("\\n\", n, s, m}'")
                .Append("; elif [ -e \"$p\" ]; then stat -L -c 'F|1|%s|%Y|").Append(i).Append("' \"$p\"")
                .Append("; else echo 'X|0|0|0|").Append(i).Append("'; fi\n");
        }

        var session = RemoteSession.Acquire(server, settings);

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var (output, _) = await session.ExecAsync(script.ToString(), cts.Token);

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
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
                items[i].Update(size, when, f[0] == "D" ? files : -1, missing: f[0] == "X");
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

        if (DownloadInputs("скачивание", "скачивать", create: true) is not (var plan, var folder))
        {
            return false;
        }

        await DownloadCoreAsync(plan, folder, CurrentOptions, VerifyChecksums);

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
    private (Dictionary<ServerEntry, List<string>> Plan, string Folder)? DownloadInputs(string what, string verb, bool create)
    {
        var picked = PickedDownloads.ToList();

        if (picked.Count == 0)
        {
            Refuse(what, Downloads.Count == 0
                ? "Список пуст — выберите файлы и папки кнопкой «Выбрать на сервере»."
                : $"Ни одна строка не отмечена — {verb} нечего.");
            return null;
        }

        var gone = picked.Where(d => d.Server is null).ToList();

        if (gone.Count > 0)
        {
            AppLog.Warn($"Строк, чей сервер удалён из списка, — {gone.Count}: они пропущены.", Title);
        }

        // по серверам; путь внутри другого из списка того же сервера скачался бы дважды
        var plan = picked
            .Where(d => d.Server is not null)
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
            Refuse(what, $"Серверы отмеченных строк удалены из списка — {verb} не с чем.");
            return null;
        }

        var folder = LocalFolder.Trim();

        if (folder.Length == 0)
        {
            Refuse(what, "Не указана папка здесь, на компьютере.");
            return null;
        }

        if (create)
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

        return (plan, folder);
    }

    /// <summary>
    /// Скачивание — одно на всю программу: его зовут и «Скачивание», и «Проводник». С одного
    /// сервера — прямо в папку, с нескольких — каждый в свою подпапку.
    /// </summary>
    public async Task DownloadCoreAsync(
        IReadOnlyDictionary<ServerEntry, List<string>> plan,
        string folder,
        UploadOptions options,
        bool verify,
        ConfirmReplace? confirmReplace = null,
        bool concurrent = false)
    {
        var targets = plan.Keys.ToList();
        var settings = AppState.Current.Settings;
        var perServer = targets.Count > 1;
        var gate = new Lock();
        var receivedMax = 0;
        var consideredMax = 0;

        AppLog.Info($"Скачивание: путей {plan.Values.Sum(p => p.Count)} с {targets.Count} сервер(ов) в {folder}.", Title);

        SetScope();
        ShowStatus("выполняется", string.Empty, WorkspaceStatus.Running);
        AppState.Current.RequestTerminal();
        ShowResults(CopyBatch);

        await CopyBatch.RunAsync(
            Operation("Скачивание"),
            targets,
            async (server, result, ct) =>
            {
                result.Message = "скачивание…";

                var root = perServer ? Path.Combine(folder, FolderName(server)) : folder;
                result.Append("куда: " + root);

                var stats = await DownloadService.DownloadAsync(
                    server, settings, plan[server], root, options, verify, line => result.Append(line), ct, result.SetTransfer, confirmReplace);

                result.Append(
                    $"итог: получено {stats.Received} ({TransferItem.Format(stats.Bytes)}) · такие же {stats.UpToDate}"
                    + (stats.Skipped > 0 ? $" · уже были, не тронуты {stats.Skipped}" : string.Empty)
                    + (stats.Dirs > 0 ? $" · папок создано {stats.Dirs}" : string.Empty));

                lock (gate)
                {
                    receivedMax = Math.Max(receivedMax, stats.Received);
                    consideredMax = Math.Max(consideredMax, stats.Received + stats.UpToDate + stats.Skipped);
                }

                if (stats.Mismatches > 0)
                {
                    result.Message = $"расхождений сумм: {stats.Mismatches}";
                    result.Status = OperationStatus.Failed;
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
            },
            settings.MaxParallel,
            concurrent: concurrent);

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
            nameof(Notices), nameof(HasNotices));

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

            if (personal.RunnableSteps(null, onlyThis: false).Count == 0)
            {
                return ["Вкладка команд пустая — после копирования выполнять нечего."];
            }

            var theirs = personal.CommandTargets;

            if (theirs.Count == 0)
            {
                return ["На вкладке команд не выбран сервер — команды после копирования не выполнятся."];
            }

            var mine = CopyTargets;

            if (!theirs.ToHashSet().SetEquals(mine))
            {
                return [$"Отличается выбор серверов у вкладки команд: там {string.Join(", ", theirs.Select(s => s.PaneLabel))}."];
            }

            return [];
        }
    }

    public bool HasNotices => Notices.Count > 0;

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

    /// <summary>
    /// Рабочие окна вкладки, пока в её окнах правят запись избранного: на диск идут они,
    /// а не правка, и вкладка ничего не выполняет.
    /// </summary>
    public List<SavedCommandStep>? HeldSteps { get; private set; }

    /// <summary>Показать в окнах чужие команды (запись избранного), отложив свои.</summary>
    public void HoldSteps(IEnumerable<SavedCommandStep> shown)
    {
        HeldSteps ??= SaveSteps();
        SetSteps(shown);
    }

    /// <summary>Вернуть свои окна.</summary>
    public void ReleaseSteps()
    {
        if (HeldSteps is not { } held)
        {
            return;
        }

        HeldSteps = null;
        SetSteps(held);
    }

    /// <summary>Все окна одним текстом — чтобы узнать «ту же команду ещё раз».</summary>
    public string StepsSignature => string.Join("\n\u241E\n", Steps.Select(s => s.Text.Trim()));

    /// <summary>Во всех окнах пусто — «Выполнить тут» и ♡♡ без дела.</summary>
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
            return $"Будет скопировано отмеченных объектов: {picked} из {Items.Count} · на серверов: {servers}.";
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
                    $"Отмечено: {Items.Count(i => i.IsSelected)} · Серверов: {SelectedServers.Count}",
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
            ? $"{Title} — {TabSummary}. Первая вкладка: её нельзя закрыть и переставить"
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

    public int AddPaths(IEnumerable<string> paths)
    {
        var added = 0;

        foreach (var path in paths)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                continue;
            }

            if (Items.Any(i => string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            Items.Add(new TransferItem(path));
            added++;
        }

        if (added > 0)
        {
            ActionLog.Info($"В список добавлено: {added} · всего {Items.Count}", ActionLog.Computer, null, this,
                Items.TakeLast(added).Select(i => (i.IsDirectory ? "папка " : "файл ") + i.FullPath));
        }

        return added;
    }

    /// <summary>
    /// Строке вписали другой путь: она встаёт на то же место заново, с прежней галочкой.
    /// Вернёт причину отказа или null, если путь сменился.
    /// </summary>
    public string? ReplaceItemPath(TransferItem item, string raw)
    {
        var path = raw.Trim().Trim('"');
        var index = Items.IndexOf(item);

        if (index < 0 || string.Equals(path, item.FullPath, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return "такого файла или папки на компьютере нет";
        }

        if (Items.Any(i => !ReferenceEquals(i, item) && string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase)))
        {
            return "этот путь уже есть в списке";
        }

        Items[index] = new TransferItem(path) { IsSelected = item.IsSelected };
        return null;
    }

    public void RemoveItems(IEnumerable<TransferItem> items)
    {
        foreach (var item in items.ToList())
        {
            Items.Remove(item);
        }
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
        if (_refreshingInfo || Items.Count == 0)
        {
            return;
        }

        _refreshingInfo = true;

        try
        {
            var snapshot = Items.ToList();

            await Task.Run(() =>
            {
                foreach (var item in snapshot)
                {
                    item.Refresh();
                }
            });

            DropMissing(snapshot);
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось обновить сведения о файлах: " + ex.Message, Title);
        }
        finally
        {
            _refreshingInfo = false;
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
        AppLog.Warn(reason, Title);
        ShowStatus("не начато", string.Empty, WorkspaceStatus.Failed, $"{what} не начато: {reason}");
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

        if (CopyInputs("копирование", "копировать") is not (var payload, var remotePath, var targets))
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
        var options = new UploadOptions(
            OverwriteFiles, PreserveTimestamps, MatchByDate, MatchByHash, ExcludeMasks, GlobalExcludeMasks);
        var verify = VerifyChecksums;

        // без переноса даты у файла на сервере стоит время заливки, а не оригинала, — тогда
        // «другая дата» будет у всего подряд и отправляться будет всё. Молчать об этом нельзя
        if (options.MatchByDate && !options.MatchByHash && !options.PreserveTimestamps)
        {
            AppLog.Warn(
                "Замена по дате изменения без переноса даты на сервер ничего не отсеет: "
                + "у серверных файлов стоит время заливки. Включите «сохранять дату изменения».",
                Title);
        }

        settings.LastRemotePath = remotePath;
        settings.PushHistory(settings.RemotePathHistory, remotePath);
        AppState.Current.SaveSettings();

        // путь ставим после правки истории: выпадающий список чистит поле ввода, когда из него
        // пропадает выбранная строка, и пустое поле уходит во вкладку — так путь и терялся
        RemotePath = remotePath;

        // файлы могли измениться с прошлого раза — считаем хеши заново и обновляем таблицу
        _localHashes.Clear();
        _ = RefreshItemsInfoAsync();

        AppLog.Info($"Копирование {payload.Count} объект(ов) в {remotePath}"
            + (options.MatchByHash ? ", только отличающиеся по контрольной сумме" : string.Empty)
            + (options.MatchByDate && !options.MatchByHash ? ", только отличающиеся по дате" : string.Empty)
            + (verify ? ", с проверкой копий" : string.Empty), Title);

        await UploadCoreAsync(payload, remotePath, targets, options, verify, prepare: ChainPlan());

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
    private (List<TransferItem> Payload, string RemotePath, IReadOnlyList<ServerEntry> Targets)? CopyInputs(string what, string verb)
    {
        var payload = Items.Where(i => i.IsSelected).ToList();

        if (payload.Count == 0)
        {
            Refuse(what, Items.Count == 0
                ? $"Список файлов пуст — {verb} нечего."
                : $"Ни один объект не отмечен галочкой — {verb} нечего.");
            return null;
        }

        var remotePath = SshService.NormalizeRemote(RemotePath);

        if (string.IsNullOrWhiteSpace(remotePath) || remotePath == "/")
        {
            Refuse(what, "Не указан путь на сервере — укажите каталог, корень «/» не принимается.");
            return null;
        }

        var targets = CopyTargets;

        if (targets.Count == 0)
        {
            Refuse(what, $"Не отмечено ни одного сервера — {verb} не с чем.");
            return null;
        }

        return (payload, remotePath, targets);
    }

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

        if (CopyInputs("сравнение", "сравнивать") is not (var payload, var remotePath, var targets))
        {
            return false;
        }

        var settings = AppState.Current.Settings;
        var options = CurrentOptions;

        // файлы могли измениться с прошлого раза — суммы заново, как перед копированием
        _localHashes.Clear();
        _ = RefreshItemsInfoAsync();

        AppLog.Info($"Сравнение {payload.Count} объект(ов) с {remotePath} — ничего не копируется.", Title);

        await CompareCoreAsync(targets, (server, result, ct) =>
        {
            result.Append($"Сравнение с {remotePath} — ничего не копируется");

            return SshService.CompareUploadAsync(
                server,
                settings,
                payload,
                remotePath,
                options,
                _localHashes,
                line => result.Append(line),
                ct,
                step => result.Message = (step switch
                {
                    UploadStep.CheckingHashes => StepCheckingHashes,
                    UploadStep.CheckingDates => StepCheckingDates,
                    _ => StepConnecting,
                }) + "…");
        }, "на сервере");

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

        if (DownloadInputs("сравнение", "сравнивать", create: false) is not (var plan, var folder))
        {
            return false;
        }

        var settings = AppState.Current.Settings;
        var options = CurrentOptions;
        var perServer = plan.Count > 1;

        AppLog.Info($"Сравнение: путей {plan.Values.Sum(p => p.Count)} с {plan.Count} сервер(ов) и папки {folder} — ничего не скачивается.", Title);

        await CompareCoreAsync(plan.Keys.ToList(), (server, result, ct) =>
        {
            var root = perServer ? Path.Combine(folder, FolderName(server)) : folder;
            result.Append($"Сравнение с {root} — ничего не скачивается");

            return DownloadService.CompareAsync(server, settings, plan[server], root, options, line => result.Append(line), ct);
        }, "здесь");

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
        string where)
    {
        var settings = AppState.Current.Settings;
        var gate = new Lock();
        var worst = 0;

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

                lock (gate)
                {
                    worst = Math.Max(worst, stats.Mismatches);
                }

                ReportCompare(result, stats, where);
            },
            settings.MaxParallel);

        if (CopyBatch.IsRunning)
        {
            return;
        }

        FinishStatus(
            CopyBatch,
            "сравнение",
            worst == 0 ? "сошлось" : $"отличий {worst}",
            worst == 0 ? "файлы одинаковые" : "что именно не сошлось — в выводе терминала");
    }

    /// <summary>
    /// Итог сравнения в строку сервера. Статус — сразу ответ «сошлось или нет»; в выводе — счёт
    /// и списки: чего нет, что отличается и чем. «Не сошлось» — не ошибка, а ответ: оранжевым.
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

        if (stats.Mismatches == 0)
        {
            result.Message = stats.Total == 0 ? "сошлось · файлов нет" : $"сошлось · одинаковых файлов {stats.Same}";
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

        result.Message = "не сошлось · " + string.Join(" · ", parts) + $" · одинаковых {stats.Same}";
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
    public async Task UploadCoreAsync(
        IReadOnlyList<TransferItem> payload,
        string remotePath,
        IReadOnlyList<ServerEntry> targets,
        UploadOptions options,
        bool verify,
        ConfirmReplace? confirmReplace = null,
        bool concurrent = false,
        Action<OperationResult>? prepare = null)
    {
        var settings = AppState.Current.Settings;

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

        await CopyBatch.RunAsync(
            Operation("Копирование"),
            targets,
            async (server, result, ct) =>
            {
                result.Message = StepConnecting + "…";
                result.Append($"Назначение: {remotePath}");

                var stats = await SshService.UploadAsync(
                    server,
                    settings,
                    payload,
                    remotePath,
                    options,
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

                var seen = stats.FilesSent + stats.FilesUpToDate + stats.FilesSkipped;

                lock (numbersLock)
                {
                    sentMax = Math.Max(sentMax, stats.FilesSent);
                    seenMax = Math.Max(seenMax, seen);
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

                var summary =
                    $"отправлено {stats.FilesSent}, {TransferItem.Format(stats.BytesSent)}"
                    + (stats.FilesUpToDate > 0 ? $" · без изменений {stats.FilesUpToDate}" : string.Empty)
                    + (stats.FilesSkipped > 0 ? $" · уже есть {stats.FilesSkipped}" : string.Empty);

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
                        result.Message = summary + $", РАСХОЖДЕНИЙ: {mismatches}";
                        result.Status = OperationStatus.Failed;
                        return;
                    }

                    result.Append($"контрольные суммы совпали: {stats.Files.Count} файл(ов)");
                    summary += ", суммы сошлись";
                }
                else
                {
                    result.Append("время: " + stats.Clock);
                }

                AppendFiles();
                result.Message = summary;
                result.Status = OperationStatus.Success;
            },
            settings.MaxParallel,
            concurrent: concurrent,
            prepare: prepare);

        // запуски «Проводника» идут рядом — итог на ярлыке, когда закончился последний
        if (CopyBatch.IsRunning)
        {
            return;
        }

        // итог задачи, а не число серверов: по «0 из 5» сразу видно, что ничего не ушло.
        // Слово «скопировано» не пишем — на узкой плашке оно не влезает, а смысл и так ясен
        var considered = seenMax > 0 ? seenMax : payload.Count;

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
        if (!_afterCopyEnabled || Personal is not { } personal || personal.HeldSteps is not null)
        {
            return null;
        }

        var steps = personal.RunnableSteps(null, onlyThis: false);
        var servers = personal.CommandTargets;
        return steps.Count == 0 || servers.Count == 0 ? null : RunHistory.CheckCommands(personal, personal.Numbered(steps), servers);
    }

    /// <summary>
    /// Строки команд после копирования — под строкой копирования каждого сервера, где они пойдут,
    /// сразу со стартом копирования: видно, что будет дальше, и любую можно остановить заранее.
    /// Null — команды после копирования не пойдут.
    /// </summary>
    private Action<OperationResult>? ChainPlan()
    {
        if (!_afterCopyEnabled || Personal is not { } personal || personal.CommandBatch.IsRunning || personal.HeldSteps is not null)
        {
            return null;
        }

        var steps = personal.RunnableSteps(null, onlyThis: false).Select(s => (s.Number, s.Text)).ToList();
        var servers = personal.CommandTargets;

        if (steps.Count == 0 || servers.Count == 0)
        {
            return null;
        }

        return row =>
        {
            if (servers.Contains(row.Server))
            {
                row.PlanSteps(steps);
            }
        };
    }

    /// <summary>
    /// Личные команды после копирования — только на тех серверах, где копирование
    /// (и сверка сумм, если она включена) прошло. Умный режим уже спросил до копирования.
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
        var missing = personal.RunnableSteps(null, onlyThis: false).Count == 0
            ? "в личной вкладке команд нет ни одной команды — впишите её"
            : personal.CommandTargets.Count == 0
                ? "в личной вкладке команд не отмечен ни один сервер — отметьте его в панели «Целевые сервера»"
                : null;

        if (missing is not null)
        {
            ChainSkipped($"{missing} (или выключите свитч на ярлыке «{Title}»)");
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

        // у личной вкладки свои отметки; сервер, где копирование не прошло, из них выпадает
        var targets = personal.CommandTargets.Where(s => !failed.Contains(s) && !stopped.Contains(s)).ToList();

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
            ChainSkipped(CopyBatch.RunSuccess == 0
                ? "копирование не прошло ни на одном сервере"
                : "на отмеченных серверах личной вкладки копирование не прошло");

            foreach (var step in personal.Steps)
            {
                step.Clear();
            }

            return;
        }

        if (failed.Count > 0)
        {
            AppLog.Info($"Команды после копирования — на {targets.Count} сервере(ах); где копирование не прошло, пропущено.", Title);
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

        AppLog.Warn(line, Title);
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

        return steps.Count == 0 || targets.Count == 0 || CommandBatch.IsRunning || HeldSteps is not null
            ? null
            : RunHistory.CheckCommands(this, Numbered(steps), targets);
    }

    /// <summary>То же для копирования.</summary>
    public RunHistory.SmartCheck? PrecheckCopy()
    {
        var targets = CopyTargets;

        return !Items.Any(i => i.IsSelected) || targets.Count == 0 || CopyBatch.IsRunning
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

        // в окнах сейчас запись избранного, а не команды вкладки (в том числе для команд после копирования)
        if (HeldSteps is not null)
        {
            return Refuse("команда", "Идёт правка избранного в окнах этой вкладки — сначала сохраните или отмените её.");
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
            settings.PushHistory(settings.CommandHistory, step.Text.Trim());
        }

        AppState.Current.SaveSettings();

        // цепочка: ненулевой код останавливает её на сервере. Одиночная команда — как в терминале:
        // что она вернула — её дело, а не ошибка программы
        var chain = steps.Count > 1;
        var numbered = Steps.Count > 1;

        AppLog.Info(
            chain
                ? $"Цепочка из {steps.Count} окон: " + string.Join(" → ", steps.Select(s => $"#{s.Number} {Preview(s.Text)}"))
                : $"Команда ({steps[0].Text.Length} символов)" + (steps[0].UseSudo ? ", через sudo" : string.Empty)
                    + ": " + Preview(steps[0].Text),
            Title);

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

                        foreach (var line in step.Text.Replace("\r\n", "\n").Split('\n'))
                        {
                            Write("$ " + line);
                        }

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
                                        $"{server.PaneLabel} ждёт ответа — введите его в строке под выводом в терминале",
                                        Title);
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
        if (CheckBatch.IsRunning)
        {
            return false;
        }

        var targets = (picked ?? SelectedServers).Where(s => !string.IsNullOrWhiteSpace(s.Host)).ToList();

        if (targets.Count == 0)
        {
            return Refuse("связь", "Не отмечено ни одного сервера — проверять нечего.");
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
            settings.MaxParallel);

        FinishStatus(
            CheckBatch,
            "связь",
            CheckBatch.RunSuccess == 0 ? "связи нет" : "связь есть");

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

        // сам по себе статус не гаснет: итог висит на ярлыке до следующей операции
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
        Downloads = Downloads.Select(d => d.ToSaved()).ToList(),
        LocalFolder = LocalFolder,
        Explorer = Explorer.Clone(),
        IsMain = IsMain,
        IsArchived = IsArchived,
        KeepOutputLog = KeepOutputLog,
        AutoCopy = _autoCopy,
        LogAsFile = _logAsFile,
        CopyPass = _copyPass,
        SelectedServerIds = CopyServers.Where(c => c.IsSelected).Select(c => c.Server.Id).ToList(),
        CommandServerIds = CommandServers.Where(c => c.IsSelected).Select(c => c.Server.Id).ToList(),
        CopyList = Items.Select(i => new SavedTransferItem { Path = i.FullPath, IsSelected = i.IsSelected }).ToList(),
        RemotePath = RemotePath,
        LastLocalFolder = LastLocalFolder,
        OverwriteFiles = OverwriteFiles,
        PreserveTimestamps = PreserveTimestamps,
        VerifyChecksums = VerifyChecksums,
        ReplaceWithoutAsking = ReplaceWithoutAsking,
        ExcludeMasks = ExcludeMasks,
        UseGlobalExcludes = UseGlobalExcludes,
        MatchByDate = MatchByDate,
        MatchByHash = MatchByHash,
        CommandText = CommandText,
        UseSudo = UseSudo,
        Commands = HeldSteps?.ToList() ?? SaveSteps(),
        PaneSetId = _paneSetId,
        PaneAllSets = _paneAllSets,
        PaneScopes = _scopes.Values.Select(v => v.Clone()).ToList(),
        PaneShowIp = _paneShowIp,
        PaneShowUser = _paneShowUser,
        LockedServerIds = _lockedIds?.ToList(),
    };

    /// <summary>Восстанавливает вкладку из файла. Пропавшие локальные пути отбрасываются.</summary>
    public void Restore(WorkspaceState state)
    {
        _remotePath = string.IsNullOrWhiteSpace(state.RemotePath) ? "/" : state.RemotePath;
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

        _localFolder = state.LocalFolder ?? string.Empty;
        Explorer = state.Explorer?.Clone() ?? new ExplorerTabState();

        _restoring = true;

        try
        {
            Downloads.Clear();

            foreach (var saved in state.Downloads ?? [])
            {
                Downloads.Add(DownloadItem.From(saved));
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
        _lockedIds = state.LockedServerIds?.ToHashSet(StringComparer.Ordinal);
        ApplyLock();
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

        var missing = 0;

        foreach (var saved in state.CopyList)
        {
            if (File.Exists(saved.Path) || Directory.Exists(saved.Path))
            {
                Items.Add(new TransferItem(saved.Path) { IsSelected = saved.IsSelected });
            }
            else
            {
                missing++;
                AppLog.Warn($"Пропал объект из списка копирования: {saved.Path}", Title);
            }
        }

        if (Items.Count > 0 || missing > 0)
        {
            AppLog.Debug($"Список копирования восстановлен: {Items.Count} объект(ов)"
                + (missing > 0 ? $", не найдено {missing}" : string.Empty), Title);
        }

        RaiseMany(
            nameof(RemotePath),
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

    private string Operation(string name) => $"{name} · {Title}";

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

        var kind = failed == 0
            ? WorkspaceStatus.Success
            : ok == 0
                ? WorkspaceStatus.Failed
                : WorkspaceStatus.Partial;

        var tooltip = $"{what}: {badge}"
            + (string.IsNullOrEmpty(note) ? string.Empty : $" · {note}")
            + $" · серверов отработало {ok} из {total}"
            + (failed > 0 ? $", с ошибкой {failed} — что за ошибка, видно в терминале" : string.Empty);

        ShowStatus(badge, failed > 0 ? $"Ошибок {failed}" : string.Empty, kind, tooltip);
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

        SyncWatchers();
        RaiseCopyInfo();
        Save();
    }

    /// <summary>
    /// Наблюдатели ровно по строкам списка: у новых заводятся, у убранных снимаются.
    /// Сверка с самим списком, а не с событием — очистка списка приходит без перечня строк.
    /// </summary>
    private void SyncWatchers()
    {
        foreach (var gone in _watchers.Keys.Where(k => !Items.Contains(k)).ToList())
        {
            _watchers[gone].Dispose();
            _watchers.Remove(gone);
        }

        foreach (var item in Items.Where(i => !_watchers.ContainsKey(i)))
        {
            _watchers[item] = new ItemWatcher(item, OnItemChangedOnDisk, OnWatchLost);
        }
    }

    /// <summary>Вкладку закрыли — следить за её файлами больше незачем.</summary>
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
            item.Refresh();
        }
        catch (Exception)
        {
            // строку перечитают при возврате в окно
        }

        if (!item.ExistsOnDisk)
        {
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => DropMissing([item])));
        }
    }

    /// <summary>
    /// Убирает из списка строки, чьих файлов и папок на диске больше нет: копировать их
    /// нечего, а висящая строка с прочерками только путает. Проверка идёт после затишья,
    /// так что редактор, сохраняющий через «удалить и переименовать», строку не теряет.
    /// </summary>
    private void DropMissing(IEnumerable<TransferItem> candidates)
    {
        var gone = candidates.Where(i => Items.Contains(i) && !i.ExistsOnDisk).ToList();

        if (gone.Count == 0)
        {
            return;
        }

        foreach (var item in gone)
        {
            Items.Remove(item);
        }

        var names = string.Join(", ", gone.Take(5).Select(i => i.Name))
            + (gone.Count > 5 ? $" и ещё {gone.Count - 5}" : string.Empty);

        AppLog.Info($"Убрано из списка — на диске больше нет: {names}.", Title);
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
            RaiseCopyInfo();
            Save();
        }
    }

    private void OnServerCheckChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ServerCheck.IsSelected))
        {
            if (_reverting)
            {
                return;
            }

            // под замком отметки не меняются ничем: ни щелчком, ни переходом, ни «Отметить все»
            if (_lockedIds is not null && sender is ServerCheck check && LockList.Contains(check)
                && check.IsSelected != _lockedIds.Contains(check.Server.Id))
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

    /// <summary>Вкладка под замком: остальные серверы и наборы приглушены, сменить сервер нельзя.</summary>
    public bool IsPaneLocked => _lockedIds is not null;

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
            ApplyLock();
            AppLog.Hint($"Вкладка «{Title}»: замок снят, сервер снова можно менять.");
            Save();
            return;
        }

        if (!clicked.IsSelected)
        {
            clicked.IsSelected = true;
        }

        var picked = LockList.Where(c => c.IsSelected).ToList();
        _lockedIds = picked.Select(c => c.Server.Id).ToHashSet(StringComparer.Ordinal);
        ApplyLock();
        AppLog.Hint($"Вкладка «{Title}» закреплена: {string.Join(", ", picked.Select(c => c.Server.PaneLabel))}. Сменить — сняв замок.");
        Save();
    }

    /// <summary>Под замком действие, меняющее серверы вкладки, не выполняется — и об этом плашка.</summary>
    private bool BlockedByLock()
    {
        if (_lockedIds is null)
        {
            return false;
        }

        // одно действие трогает много отметок — плашка одна
        if (DateTime.UtcNow - _lockHintAt > TimeSpan.FromSeconds(2))
        {
            _lockHintAt = DateTime.UtcNow;
            AppLog.Hint($"Вкладка «{Title}» закреплена за своим сервером — сменить можно, сняв замок в его строке.");
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

        Raise(nameof(IsPaneLocked));
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

        var present = CopyServers.Select(c => c.Server.Id).ToHashSet(StringComparer.Ordinal);
        _lockedIds.IntersectWith(present);

        if (_lockedIds.Count == 0)
        {
            _lockedIds = null;
            AppLog.Hint($"Вкладка «{Title}»: закреплённого сервера больше нет — замок снят.");
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
        nameof(TargetsInfo),
        nameof(DownloadInfo),
        nameof(AllItemsSelected),
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
