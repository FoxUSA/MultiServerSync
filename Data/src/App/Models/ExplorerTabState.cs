namespace MultiServerSync.Models;

/// <summary>
/// Что помнит вкладка «Проводника»: обе папки, строка под курсором, сортировка, маски,
/// дерево и ширина панелей. При следующем открытии всё стоит там, где оставили.
/// </summary>
public sealed class ExplorerTabState
{
    public ExplorerPaneState Local { get; set; } = new();

    public ExplorerPaneState Remote { get; set; } = new();

    /// <summary>Сервер, к которому относится папка справа: сменили сервер — папка с другого не годится.</summary>
    public string RemoteServerId { get; set; } = string.Empty;

    /// <summary>Доля ширины левой панели, 0.2–0.8.</summary>
    public double Split { get; set; } = 0.5;

    /// <summary>Какая панель была активной: true — сервер.</summary>
    public bool RemoteActive { get; set; } = true;

    public ExplorerTabState Clone() => new()
    {
        Local = Local.Clone(),
        Remote = Remote.Clone(),
        RemoteServerId = RemoteServerId,
        Split = Split,
        RemoteActive = RemoteActive,
    };
}

/// <summary>Память одной панели проводника.</summary>
public sealed class ExplorerPaneState
{
    /// <summary>Открытая папка. Пусто: на компьютере — «Этот компьютер», на сервере — домашняя.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Имя строки под курсором — по нему курсор встаёт на место.</summary>
    public string Focus { get; set; } = string.Empty;

    /// <summary>Выделенное (отмеченное галочками) в открытой папке — сам выбор человека.</summary>
    public List<string> Selected { get; set; } = new();

    public string SortKey { get; set; } = "Name";

    public bool SortDescending { get; set; }

    public string FileMask { get; set; } = string.Empty;

    public bool ShowTree { get; set; }

    /// <summary>Ширины столбцов по имени столбца.</summary>
    public Dictionary<string, double> Columns { get; set; } = new();

    /// <summary>Скрытые столбцы.</summary>
    public List<string> HiddenColumns { get; set; } = new();

    public ExplorerPaneState Clone() => new()
    {
        Path = Path,
        Focus = Focus,
        Selected = Selected.ToList(),
        SortKey = SortKey,
        SortDescending = SortDescending,
        FileMask = FileMask,
        ShowTree = ShowTree,
        Columns = new Dictionary<string, double>(Columns),
        HiddenColumns = HiddenColumns.ToList(),
    };
}
