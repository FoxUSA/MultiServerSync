namespace MultiServerSync.Models;

/// <summary>
/// Вкладка-рабочий стол в том виде, в каком она лежит в UserData\workspaces.json.
/// Сами серверы тут не дублируются — только их идентификаторы: список серверов общий.
/// </summary>
public sealed class WorkspaceState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Title { get; set; } = "Вкладка";

    /// <summary>Первая вкладка: её нельзя закрыть.</summary>
    public bool IsMain { get; set; }

    /// <summary>Серверы, отмеченные в разделе «Копирование».</summary>
    public List<string> SelectedServerIds { get; set; } = new();

    /// <summary>
    /// Серверы, отмеченные в разделе «Команда» — набор свой, а не общий с копированием.
    /// null означает файл старого формата: тогда берётся набор копирования.
    /// </summary>
    public List<string>? CommandServerIds { get; set; }

    public List<SavedTransferItem> CopyList { get; set; } = new();

    public string RemotePath { get; set; } = "/";

    /// <summary>Папка, из которой в эту вкладку последний раз добавляли файлы.</summary>
    public string LastLocalFolder { get; set; } = string.Empty;

    public bool OverwriteFiles { get; set; } = true;

    public bool PreserveTimestamps { get; set; } = true;

    /// <summary>Сверять суммы после копирования — проверка того, что уже отправлено.</summary>
    public bool VerifyChecksums { get; set; }

    /// <summary>Исключения при копировании — маски по одной в строке, см. ExcludeRules.</summary>
    public string ExcludeMasks { get; set; } = string.Empty;

    /// <summary>Заменять файлы с другой датой изменения.</summary>
    public bool MatchByDate { get; set; }

    /// <summary>Заменять файлы с другой контрольной суммой.</summary>
    public bool MatchByHash { get; set; }

    /// <summary>
    /// Прежнее имя «матча по дате» в файлах старых версий. Только на чтение при загрузке:
    /// getter'а нет намеренно, чтобы поле не попадало обратно в json.
    /// </summary>
    public bool OnlyNewer
    {
        set
        {
            if (value)
            {
                MatchByDate = true;
            }
        }
    }

    public string CommandText { get; set; } = string.Empty;

    public bool UseSudo { get; set; }

    /// <summary>
    /// Окна многозадачности по порядку. Пусто — файл старой версии: тогда окно одно,
    /// из <see cref="CommandText"/> и <see cref="UseSudo"/> (они же пишутся из первого окна).
    /// </summary>
    public List<SavedCommandStep> Commands { get; set; } = new();

    /// <summary>Вкладка копирования: её личная командная вкладка (выполняется после удачного копирования).</summary>
    public string AfterCopyTabId { get; set; } = string.Empty;

    /// <summary>Свитч на ярлыке копирования: выполнять ли личные команды после копирования.</summary>
    public bool AfterCopyEnabled { get; set; }

    /// <summary>Личная командная вкладка: чья она.</summary>
    public string OwnerCopyTabId { get; set; } = string.Empty;

    /// <summary>Вкладка скачивания: что скачивать — строки со своим сервером и галочкой.</summary>
    public List<SavedDownloadItem> Downloads { get; set; } = new();

    /// <summary>Вкладка проводника: папки обеих панелей, курсор, сортировка, маски.</summary>
    public ExplorerTabState? Explorer { get; set; }

    /// <summary>Вкладка скачивания: куда скачивать.</summary>
    public string LocalFolder { get; set; } = string.Empty;

    /// <summary>
    /// Набор серверов, из которого вкладка выбирает цели. null — файл прежней версии, где набор
    /// был общий на все вкладки: тогда вкладка получает тот, что был выбран.
    /// </summary>
    public string? PaneSetId { get; set; }

    /// <summary>У вкладки выбрано «Все» — серверы всех наборов.</summary>
    public bool PaneAllSets { get; set; }

    /// <summary>
    /// Память панели по каждому набору и по «Всем»: одиночный выбор, свои отметки и т. д.
    /// null — файл прежней версии.
    /// </summary>
    public List<PaneScopeState>? PaneScopes { get; set; }

    /// <summary>Галочки «IP» и «User» под панелью серверов. null — файл прежней версии, где они были у раздела.</summary>
    public bool? PaneShowIp { get; set; }

    public bool? PaneShowUser { get; set; }

    /// <summary>
    /// Версия 2.0.8: один одиночный выбор на все наборы вкладки и его память. Только на чтение
    /// для переноса в <see cref="PaneScopes"/>; getter'ов нет, чтобы поля не попадали обратно в json.
    /// </summary>
    public bool? SingleSelect { set => LegacySingleSelect = value; }

    public List<string>? MultiServerIds { set => LegacyMultiServerIds = value; }

    public string? SinglePickId { set => LegacySinglePickId = value; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool? LegacySingleSelect { get; private set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public List<string>? LegacyMultiServerIds { get; private set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string? LegacySinglePickId { get; private set; }
}

/// <summary>Память панели вкладки в одном наборе (Key — Id набора) или на «Всех» (Key — «*»).</summary>
public sealed class PaneScopeState
{
    public string Key { get; set; } = string.Empty;

    /// <summary>Свитч «Одиночный выбор» в этом наборе.</summary>
    public bool SingleSelect { get; set; }

    /// <summary>Отметки, какими они были до включения одиночного выбора; выключат — вернутся.</summary>
    public List<string>? MultiServerIds { get; set; }

    /// <summary>Сервер, выбранный в одиночном режиме в прошлый раз.</summary>
    public string? SinglePickId { get; set; }

    /// <summary>Отметки на момент ухода в другой набор: вернулись — встают они.</summary>
    public List<string>? PickedServerIds { get; set; }

    public PaneScopeState Clone() => new()
    {
        Key = Key,
        SingleSelect = SingleSelect,
        MultiServerIds = MultiServerIds?.ToList(),
        SinglePickId = SinglePickId,
        PickedServerIds = PickedServerIds?.ToList(),
    };
}

/// <summary>Файл вкладок целиком.</summary>
public sealed class WorkspacesFile
{
    public string ActiveId { get; set; } = string.Empty;

    public List<WorkspaceState> Workspaces { get; set; } = new();

    /// <summary>Открытая вкладка раздела «Команда».</summary>
    public string ActiveCommandId { get; set; } = string.Empty;

    /// <summary>
    /// Вкладки раздела «Команда» — свои, отдельные от копирования. Используются только
    /// поля команды. Пусто — файл старой версии, где команда жила на основной вкладке.
    /// </summary>
    public List<WorkspaceState> CommandTabs { get; set; } = new();

    /// <summary>Открытая вкладка раздела «Скачивание».</summary>
    public string ActiveDownloadId { get; set; } = string.Empty;

    /// <summary>Вкладки раздела «Скачивание». Пусто — файл версии, где раздела ещё не было.</summary>
    public List<WorkspaceState> DownloadTabs { get; set; } = new();

    /// <summary>Открытая вкладка раздела «Проводник».</summary>
    public string ActiveExplorerId { get; set; } = string.Empty;

    /// <summary>Вкладки раздела «Проводник». Пусто — файл версии, где раздела ещё не было.</summary>
    public List<WorkspaceState> ExplorerTabs { get; set; } = new();
}
