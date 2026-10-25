namespace MultiServerSync.Models;

/// <summary>
/// Вкладка раздела «Серверы» в том виде, в каком она лежит в UserData\serversets.json.
/// Серверы тут не перечисляются: каждый сам помнит свой набор (<see cref="ServerEntry.SetId"/>).
/// </summary>
public sealed class ServerSetState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Title { get; set; } = "Набор";

    /// <summary>
    /// «Одиночный выбор» на «Команде»: отметить можно только один сервер набора.
    /// null — файл старой версии, тогда включено.
    /// </summary>
    public bool? SingleSelect { get; set; }

    /// <summary>«Одиночный выбор» на «Копировании» — свой, отдельный от командного.</summary>
    public bool SingleSelectCopy { get; set; }
}

/// <summary>Файл наборов целиком.</summary>
public sealed class ServerSetsFile
{
    public string ActiveId { get; set; } = string.Empty;

    /// <summary>
    /// Прежние общие «Все» и одиночный выбор на «Всех». Теперь это своё у каждой вкладки
    /// (<see cref="WorkspaceState.PaneAllSets"/>), здесь — только чтобы перенести во вкладки
    /// из файла прежней версии. Не записываются: getter'ов нет, как у <see cref="WorkspaceState.OnlyNewer"/>.
    /// </summary>
    public bool ShowAll { set => LegacyShowAll = value; }

    public bool? AllSingleSelect { set => LegacyAllSingleSelect = value; }

    public bool AllSingleSelectCopy { set => LegacyAllSingleSelectCopy = value; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool LegacyShowAll { get; private set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool? LegacyAllSingleSelect { get; private set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool LegacyAllSingleSelectCopy { get; private set; }

    public List<ServerSetState> Sets { get; set; } = new();
}
