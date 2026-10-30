using System.Collections.ObjectModel;
using System.ComponentModel;
using MultiServerSync.Core;
using MultiServerSync.Models;

namespace MultiServerSync.Services;

/// <summary>
/// Вкладка раздела «Серверы»: свой, ни с чем не связанный список серверов.
/// Выбранный набор целиком показывается в панели «Целевые сервера» слева,
/// а копирование и команда работают только с ним.
/// Галочки в таблице набора — только для действий на этой странице:
/// удалить, дублировать, проверить связь. Куда копировать, они не решают.
/// </summary>
public sealed class ServerSet : ObservableObject
{
    private string _title;
    private bool _singleSelect = true;
    private bool _singleSelectCopy;

    public ServerSet(string id, string title)
    {
        Id = id;
        _title = title;
    }

    public string Id { get; }

    private bool _isArchived;

    /// <summary>
    /// Ярлык в архиве под «+», а не в ленте. Набор работает как прежде: его видят вкладки,
    /// его можно открыть в таблице. Сохраняет AppState.
    /// </summary>
    public bool IsArchived
    {
        get => _isArchived;
        set => Set(ref _isArchived, value);
    }

    /// <summary>
    /// Закрыть можно любой набор, кроме последнего в основной ленте: без наборов серверам
    /// негде жить. Закреплённого «первого» нет — наборы переставляются мышью все.
    /// </summary>
    public bool CanClose => IsArchived || AppState.Current.ServerSets.Count(s => !s.IsArchived) > 1;

    public void RaiseCanClose() => RaiseMany(nameof(CanClose), nameof(TabTooltip));

    public string Title
    {
        get => _title;
        set
        {
            if (Set(ref _title, string.IsNullOrWhiteSpace(value) ? "Набор" : value.Trim()))
            {
                Raise(nameof(TabTooltip));
                AppState.Current.SaveServerSets();
            }
        }
    }

    /// <summary>
    /// «Одиночный выбор» на «Команде», как он хранился у набора в прежних версиях. Теперь
    /// одиночный выбор свой у каждой вкладки (<see cref="Workspace.SingleSelect"/>), а это значение
    /// нужно только один раз — перенести его во вкладки, сохранённые без своего.
    /// </summary>
    public bool SingleSelect => _singleSelect;

    /// <summary>То же для «Копирования» — тоже только для переноса во вкладки.</summary>
    public bool SingleSelectCopy => _singleSelectCopy;

    /// <summary>Из файла: без сохранения и без уведомлений.</summary>
    public void RestoreSingleSelect(bool value, bool copy)
    {
        _singleSelect = value;
        _singleSelectCopy = copy;
    }

    /// <summary>Строки таблицы на странице «Серверы» с галочками для действий над ними.</summary>
    public ObservableCollection<ServerCheck> Rows { get; } = new();

    public IReadOnlyList<ServerEntry> Servers => Rows.Select(r => r.Server).ToList();

    public IReadOnlyList<ServerEntry> Picked => Rows.Where(r => r.IsSelected).Select(r => r.Server).ToList();

    public bool Contains(ServerEntry server) => server.SetId == Id;

    /// <summary>Состояние галочки в шапке таблицы: все / ничего / частично.</summary>
    public bool? AllSelected
    {
        get
        {
            var picked = Rows.Count(r => r.IsSelected);

            if (Rows.Count == 0 || picked == 0)
            {
                return false;
            }

            return picked == Rows.Count ? true : null;
        }
    }

    public string SelectionInfo => $"всего: {Rows.Count} · отмечено: {Rows.Count(r => r.IsSelected)}";

    public string TabCounts => $"серверов {Rows.Count}";

    public string TabTooltip => CanClose
        ? $"{Title} — {TabCounts}"
        : $"{Title} — {TabCounts}. Последний набор в ленте: его нельзя закрыть";

    /// <summary>Открытый сейчас набор — ярлык подсвечен.</summary>
    public bool IsActive => ReferenceEquals(this, AppState.Current.ActiveServerSet);

    /// <summary>
    /// Набор отмечен в переключателе слева: его выбрала открытая вкладка, и у неё не стоит «Все».
    /// Это не <see cref="IsActive"/> — тот про набор, который правится на странице «Серверы».
    /// </summary>
    public bool IsPanePick => ReferenceEquals(this, AppState.Current.PaneSet) && !AppState.Current.PaneAllSets;

    public void RaiseActive() => RaiseMany(nameof(IsActive), nameof(IsPanePick));

    /// <summary>Подгоняет строки под общий список серверов, сохраняя расставленные галочки.</summary>
    public void Sync(IList<ServerEntry> all)
    {
        var mine = all.Where(Contains).ToList();

        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (!mine.Contains(Rows[i].Server))
            {
                Rows[i].PropertyChanged -= OnRowChanged;
                Rows.RemoveAt(i);
            }
        }

        for (var i = 0; i < mine.Count; i++)
        {
            var existing = Rows.FirstOrDefault(r => ReferenceEquals(r.Server, mine[i]));

            if (existing is null)
            {
                var row = new ServerCheck(mine[i], isSelected: false);
                row.PropertyChanged += OnRowChanged;
                Rows.Insert(Math.Min(i, Rows.Count), row);
                continue;
            }

            var index = Rows.IndexOf(existing);

            if (index != i && i < Rows.Count)
            {
                Rows.Move(index, i);
            }
        }

        RaiseCounts();
    }

    public void ToggleAll()
    {
        var selectAll = Rows.Any(r => !r.IsSelected);

        foreach (var row in Rows)
        {
            row.IsSelected = selectAll;
        }

        RaiseCounts();
    }

    public ServerSetState ToState() => new()
    {
        Id = Id,
        Title = Title,
        SingleSelect = SingleSelect,
        SingleSelectCopy = SingleSelectCopy,
        Archived = IsArchived,
    };

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ServerCheck.IsSelected))
        {
            RaiseCounts();
        }
    }

    private void RaiseCounts() => RaiseMany(
        nameof(AllSelected),
        nameof(SelectionInfo),
        nameof(TabCounts),
        nameof(TabTooltip));
}
