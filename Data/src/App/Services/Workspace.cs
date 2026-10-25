using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
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
    private string _excludeMasks = string.Empty;
    private bool _matchByDate;
    private bool _matchByHash;
    private string _commandText = string.Empty;
    private bool _useSudo;

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
    }

    public string Id { get; }

    /// <summary>Первая вкладка. Её нельзя закрыть; в остальном она такая же, как прочие.</summary>
    public bool IsMain { get; }

    public bool CanClose => !IsMain;

    public string Title
    {
        get => _title;
        set
        {
            if (Set(ref _title, string.IsNullOrWhiteSpace(value) ? "Вкладка" : value.Trim()))
            {
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
                scope = new PaneScopeState { Key = key, SingleSelect = AppState.Current.CommandTabs.Contains(this) };
                _scopes[key] = scope;
            }

            return scope;
        }
    }

    public void PickPaneSet(ServerSet set) => SwitchScope(() =>
    {
        _paneSetId = set.Id;
        _paneAllSets = false;
    });

    public void PickAllSets() => SwitchScope(() => _paneAllSets = true);

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

        if (scope.SingleSelect == value)
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

    /// <summary>Исключения при копировании: маски по одной в строке.</summary>
    public string ExcludeMasks
    {
        get => _excludeMasks;
        set { if (Set(ref _excludeMasks, value ?? string.Empty)) { Raise(nameof(ExcludeCaption)); Save(); } }
    }

    /// <summary>Подпись кнопки: сразу видно, что на вкладке что-то исключается.</summary>
    public string ExcludeCaption => ExcludeRules.Count(_excludeMasks) is var n and > 0
        ? $"Исключения · {n}"
        : "Исключения";

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

    public string CommandText
    {
        get => _commandText;
        set { if (Set(ref _commandText, value)) { RaiseMany(nameof(CommandSizeInfo), nameof(TabCounts), nameof(TabTooltip)); Save(); } }
    }

    public bool UseSudo
    {
        get => _useSudo;
        set { if (Set(ref _useSudo, value)) { Save(); } }
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
        (CommandSet ? "для команды" : "для копирования")
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
            var text = CommandText ?? string.Empty;

            if (text.Length == 0)
            {
                return string.Empty;
            }

            var lines = text.Split('\n').Length;
            return $"символов: {text.Length} · строк: {lines}";
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
                AppSection.Command => string.IsNullOrWhiteSpace(CommandText)
                    ? $"{servers} · нет команды"
                    : $"{servers} · {CommandText.Trim().Length} симв.",
                _ => $"{servers} из {Servers.Count}",
            };
        }
    }

    public void RaiseTabCounts() => RaiseMany(nameof(TabCounts), nameof(TabTooltip));

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
        : IsMain
            ? $"{Title} — {TabCounts}. Первая вкладка: её нельзя закрыть и переставить"
            : $"{Title} — {TabCounts}. Перетащите мышью, чтобы поменять вкладки местами";

    /// <summary>
    /// Статус на ярлыке цветом не выделяется — только жирным шрифтом. Само значение
    /// нужно, чтобы знать, есть ли что показывать.
    /// </summary>
    public WorkspaceStatus Status
    {
        get => _status;
        private set { if (Set(ref _status, value)) { Raise(nameof(HasStatus)); } }
    }

    public bool HasStatus => Status != WorkspaceStatus.None;

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
        var set = Servers;
        var selectAll = set.Any(s => !s.IsSelected);

        foreach (var check in set)
        {
            check.IsSelected = selectAll;
        }

        RaiseSelectionInfo();
        AppLog.Info(selectAll ? $"Отмечены все целевые сервера: {set.Count}." : "Отметки целевых серверов сняты.", Title);
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
            AppLog.Info($"В список добавлено объектов: {added} (всего {Items.Count}).", Title);
        }

        return added;
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
    /// Операция не начата. Причину должно быть видно сразу: она уходит в журнал, разворачивает его
    /// внизу окна и остаётся меткой на ярлыке вкладки. Иначе нажатие кнопки выглядит как
    /// «ничего не произошло» — особенно при «Общей», когда отказали все вкладки сразу.
    /// </summary>
    private bool Refuse(string what, string reason)
    {
        AppLog.Warn(reason, Title);
        ShowStatus("не начато", string.Empty, WorkspaceStatus.Failed, $"{what} не начато: {reason}");
        AppState.Current.RequestLog();
        return false;
    }

    /// <summary>Копирование отмеченных файлов на отмеченные серверы этой вкладки.</summary>
    /// <param name="notify">
    /// Сообщить об окончании (автокопия вывода). «Выполнить везде» сообщает само — один раз по сводке.
    /// </param>
    public async Task<bool> RunCopyAsync(bool notify = true)
    {
        if (CopyBatch.IsRunning)
        {
            // метку «идёт» не сбиваем — операция та же самая и она ещё работает
            AppLog.Warn("Копирование уже идёт.", Title);
            AppState.Current.RequestLog();
            return false;
        }

        var payload = Items.Where(i => i.IsSelected).ToList();

        if (payload.Count == 0)
        {
            return Refuse("копирование", Items.Count == 0
                ? "Список файлов пуст — копировать нечего."
                : "Ни один объект не отмечен галочкой — копировать нечего.");
        }

        var remotePath = SshService.NormalizeRemote(RemotePath);

        if (string.IsNullOrWhiteSpace(remotePath) || remotePath == "/")
        {
            return Refuse("копирование",
                "Не указан путь на сервере — укажите каталог, корень «/» не принимается.");
        }

        var targets = CopyTargets;

        if (targets.Count == 0)
        {
            return Refuse("копирование", "Не отмечено ни одного сервера для копирования.");
        }

        // умный режим: сервер непривычный для этой вкладки — сначала спросить
        if (!RunHistory.ApproveCopy(this, targets))
        {
            return false;
        }

        var settings = AppState.Current.Settings;
        var options = new UploadOptions(OverwriteFiles, PreserveTimestamps, MatchByDate, MatchByHash, ExcludeMasks);
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
                    });

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
                // теряются среди пропусков и замен
                if (stats.Files.Count > 0)
                {
                    result.Append($"── скопированы файлы ({stats.Files.Count}) ──");

                    foreach (var file in stats.Files)
                    {
                        result.Append($"→ {file.RemotePath}  ({TransferItem.Format(file.Size)})");
                    }
                }
                else
                {
                    result.Append("── скопированных файлов нет ──");
                }

                var summary =
                    $"отправлено {stats.FilesSent}, {TransferItem.Format(stats.BytesSent)}"
                    + (stats.FilesUpToDate > 0 ? $" · без изменений {stats.FilesUpToDate}" : string.Empty)
                    + (stats.FilesSkipped > 0 ? $" · уже есть {stats.FilesSkipped}" : string.Empty);

                if (verify && stats.Files.Count > 0)
                {
                    result.Message = StepVerifying + "…";
                    Step(StepVerifyingOrder, StepVerifying);

                    var mismatches = await SshService.VerifyChecksumsAsync(
                        server, settings, stats.Files, _localHashes, line => result.Append(line), ct);

                    if (mismatches > 0)
                    {
                        result.Message = summary + $", РАСХОЖДЕНИЙ: {mismatches}";
                        result.Status = OperationStatus.Failed;
                        return;
                    }

                    result.Append($"контрольные суммы совпали: {stats.Files.Count} файл(ов)");
                    summary += ", суммы сошлись";
                }

                result.Message = summary;
                result.Status = OperationStatus.Success;
            },
            settings.MaxParallel);

        // итог задачи, а не число серверов: по «0 из 5» сразу видно, что ничего не ушло.
        // Слово «скопировано» не пишем — на узкой плашке оно не влезает, а смысл и так ясен
        var considered = seenMax > 0 ? seenMax : payload.Count;

        FinishStatus(
            CopyBatch,
            "скопировано",
            $"{sentMax} из {considered}",
            sentMax == 0 && CopyBatch.FailedCount == 0
                ? "отправлять было нечего: на серверах файлы такие же или свежее"
                : null);

        if (notify)
        {
            AppState.Current.NotifyFinished(CopyBatch);
        }

        return true;
    }

    /// <summary>Выполнение команды этой вкладки на её отмеченных серверах.</summary>
    /// <param name="notify">
    /// Сообщить об окончании (автокопия вывода). «Выполнить везде» сообщает само — один раз по сводке.
    /// </param>
    public async Task<bool> RunCommandAsync(bool notify = true)
    {
        if (CommandBatch.IsRunning)
        {
            AppLog.Warn("Команда уже выполняется.", Title);
            AppState.Current.RequestLog();
            return false;
        }

        var command = (CommandText ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(command))
        {
            return Refuse("команда", "Команда пустая.");
        }

        var targets = CommandTargets;

        if (targets.Count == 0)
        {
            return Refuse("команда", "Не отмечено ни одного сервера для команды.");
        }

        // умный режим: сервер непривычный для этой команды — сначала спросить
        if (!RunHistory.ApproveCommand(this, command, targets))
        {
            return false;
        }

        var settings = AppState.Current.Settings;
        settings.PushHistory(settings.CommandHistory, command);
        AppState.Current.SaveSettings();

        var useSudo = UseSudo;

        AppLog.Info($"Команда ({command.Length} символов)" + (useSudo ? ", через sudo" : string.Empty)
            + ": " + Preview(command), Title);

        SetScope();
        ShowStatus("выполняется", string.Empty, WorkspaceStatus.Running);
        AppState.Current.RequestTerminal();
        ShowResults(CommandBatch);

        // Ctrl+C посылается отдельным подключением, и под sudo процессы команды принадлежат root
        CommandBatch.UsedSudo = useSudo;

        await CommandBatch.RunAsync(
            Operation("Выполнение команды"),
            targets,
            async (server, result, ct) =>
            {
                result.Message = "выполняется…";

                foreach (var line in command.Replace("\r\n", "\n").Split('\n'))
                {
                    result.Append("$ " + line);
                }

                var outcome = await SshService.RunScriptAsync(
                    server,
                    settings,
                    command,
                    useSudo,
                    line => result.Append(line),
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
                        result.Message = waiting ? "ждёт ответа" : "выполняется…";

                        if (waiting)
                        {
                            AppLog.Warn(
                                $"{server.PaneLabel} ждёт ответа — введите его в строке под выводом в терминале",
                                Title);
                        }
                    },
                    tail => result.SetTail(tail));

                // Ошибка — это когда команду не удалось выполнить: не достучались, оборвало,
                // не дождались. Что команда написала в вывод и чем закончилась — её дело:
                // терминал за это красным не красит, и мы не красим.
                // оборвали нашей же кнопкой — это не ошибка, а выполненная просьба.
                // Проверяется до всего прочего: после kill сервер может и не успеть
                // сообщить, каким сигналом всё закончилось
                if (result.Interrupted && !outcome.TimedOut)
                {
                    result.Append("— команда прервана —");
                    result.Message = "прервано";
                    result.Status = OperationStatus.Cancelled;
                    AppLog.Warn("команда прервана", server.Display);
                    return;
                }

                if (outcome.Interruption is { } trouble)
                {
                    result.Append("— " + trouble + " —");
                    result.Message = outcome.TimedOut
                        ? $"не дождались ответа ({settings.CommandTimeoutSeconds} с)"
                        : trouble;
                    result.Status = OperationStatus.Failed;
                    AppLog.Error(result.Message, server.Display);
                    return;
                }

                result.Message = string.Empty;
                result.Status = OperationStatus.Success;
            },
            settings.MaxParallel);

        FinishStatus(
            CommandBatch,
            "команда",
            CommandBatch.SuccessCount == 0 ? "не выполнено" : "выполнено");

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
            CheckBatch.SuccessCount == 0 ? "связи нет" : "связь есть");

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
        IsMain = IsMain,
        SelectedServerIds = CopyServers.Where(c => c.IsSelected).Select(c => c.Server.Id).ToList(),
        CommandServerIds = CommandServers.Where(c => c.IsSelected).Select(c => c.Server.Id).ToList(),
        CopyList = Items.Select(i => new SavedTransferItem { Path = i.FullPath, IsSelected = i.IsSelected }).ToList(),
        RemotePath = RemotePath,
        LastLocalFolder = LastLocalFolder,
        OverwriteFiles = OverwriteFiles,
        PreserveTimestamps = PreserveTimestamps,
        VerifyChecksums = VerifyChecksums,
        ExcludeMasks = ExcludeMasks,
        MatchByDate = MatchByDate,
        MatchByHash = MatchByHash,
        CommandText = CommandText,
        UseSudo = UseSudo,
        PaneSetId = _paneSetId,
        PaneAllSets = _paneAllSets,
        PaneScopes = _scopes.Values.Select(v => v.Clone()).ToList(),
        PaneShowIp = _paneShowIp,
        PaneShowUser = _paneShowUser,
    };

    /// <summary>Восстанавливает вкладку из файла. Пропавшие локальные пути отбрасываются.</summary>
    public void Restore(WorkspaceState state)
    {
        _remotePath = string.IsNullOrWhiteSpace(state.RemotePath) ? "/" : state.RemotePath;
        _lastLocalFolder = state.LastLocalFolder ?? string.Empty;
        _overwrite = state.OverwriteFiles;
        _preserveTimestamps = state.PreserveTimestamps;
        _verifyChecksums = state.VerifyChecksums;
        _excludeMasks = state.ExcludeMasks ?? string.Empty;
        _matchByDate = state.MatchByDate;
        _matchByHash = state.MatchByHash;
        _commandText = state.CommandText ?? string.Empty;
        _useSudo = state.UseSudo;
        _paneSetId = state.PaneSetId ?? string.Empty;
        _paneAllSets = state.PaneAllSets;
        _paneShowIp = state.PaneShowIp ?? false;
        _paneShowUser = state.PaneShowUser ?? false;
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
            AppLog.Info($"Список копирования восстановлен: {Items.Count} объект(ов)"
                + (missing > 0 ? $", не найдено {missing}" : string.Empty), Title);
        }

        RaiseMany(
            nameof(RemotePath),
            nameof(OverwriteFiles),
            nameof(PreserveTimestamps),
            nameof(VerifyChecksums),
            nameof(ExcludeMasks),
            nameof(ExcludeCaption),
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
        var scope = AppState.Current.TabsOf(this).Count > 1 ? Title : null;

        CopyBatch.Scope = scope;
        CommandBatch.Scope = scope;
        CheckBatch.Scope = scope;
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
        var total = batch.Total;
        var ok = batch.SuccessCount;
        var failed = batch.FailedCount;

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
            if (string.Equals(key, item.FullPath, StringComparison.OrdinalIgnoreCase)
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

    /// <summary>Одиночный выбор действует на «Команде» и «Копировании»; на «Серверах» галочки обычные.</summary>
    public bool IsPaneSingleSelect =>
        SingleSelect && AppState.Current.Section is AppSection.Command or AppSection.Files;

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
        AppLog.Info($"Одиночный выбор: оставлен один сервер — {picked[0].Server.PaneLabel}.", Title);
    }

    private void RaiseCopyInfo() => RaiseMany(
        nameof(TargetsInfo),
        nameof(AllItemsSelected),
        nameof(TabCounts),
        nameof(TabTooltip));

    private void RaiseSelectionInfo() => RaiseMany(
        nameof(SelectionInfo),
        nameof(PaneAllChecked),
        nameof(PaneTitle),
        nameof(PaneEmptyHintVisibility),
        nameof(TargetsInfo),
        nameof(CommandTargetsInfo),
        nameof(AllServersSelected),
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
