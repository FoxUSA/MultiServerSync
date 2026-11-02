using MultiServerSync.Services;

namespace MultiServerSync.Models;

/// <summary>Строка терминала на диске: сервер, итог и весь её вывод, с командами цепочки.</summary>
public sealed class TerminalRowState
{
    public string ServerId { get; set; } = string.Empty;

    public string WindowLabel { get; set; } = string.Empty;

    public string Scope { get; set; } = string.Empty;

    public DateTime Created { get; set; }

    public OperationStatus Status { get; set; }

    public string Message { get; set; } = string.Empty;

    public double DurationMs { get; set; }

    /// <summary>Строка разового действия с оговоркой — оранжевый треугольник, а не «отменено».</summary>
    public bool Warning { get; set; }

    /// <summary>Строка разового действия, не запуска: автокопия её не берёт.</summary>
    public bool IsAction { get; set; }

    /// <summary>Номер запуска строки — по нему таблица собирает строки одного нажатия вместе.</summary>
    public long RunId { get; set; }

    public DateTime? FinishedUtc { get; set; }

    public bool Skipped { get; set; }

    public string Output { get; set; } = string.Empty;

    public int StepNumber { get; set; }

    public string StepCommand { get; set; } = string.Empty;

    public List<TerminalRowState>? Steps { get; set; }
}

/// <summary>Один прогон вкладки (копирование, команда, проверка, сравнение) со сроком жизни вывода.</summary>
public sealed class TerminalBatchState
{
    public List<TerminalRowState> Rows { get; set; } = new();

    /// <summary>С какого момента идёт «Хранить лог»; после перезапуска отсчёт продолжается от него.</summary>
    public DateTime? LifeFrom { get; set; }

    public DateTime? FinishedAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public int Total { get; set; }

    public int Done { get; set; }

    public int Success { get; set; }

    public int Failed { get; set; }
}

public sealed class TerminalTabState
{
    public Dictionary<string, TerminalBatchState> Batches { get; set; } = new();

    /// <summary>Какой прогон показывал терминал вкладки.</summary>
    public string? Shown { get; set; }
}

/// <summary>UserData\terminal.json: терминалы всех вкладок, по Id вкладки.</summary>
public sealed class TerminalFile
{
    public DateTime SavedAt { get; set; }

    public Dictionary<string, TerminalTabState> Tabs { get; set; } = new();
}

/// <summary>
/// Действие с вкладкой для отмены — UserData\tab-actions.json. Вкладка, которой сейчас нет в лентах,
/// лежит здесь целиком, без вывода терминала: иначе после перезапуска её было бы неоткуда вернуть.
/// </summary>
public sealed class TabActionState
{
    public AppSection Section { get; set; }

    /// <summary>Уже отменено: шаг ждёт стрелки «вернуть».</summary>
    public bool Undone { get; set; }

    public string What { get; set; } = string.Empty;

    public DateTime At { get; set; }

    public string TabId { get; set; } = string.Empty;

    /// <summary>Переименование: прежнее и новое название. Нет — вкладку добавили или закрыли.</summary>
    public string? Before { get; set; }

    public string? After { get; set; }

    /// <summary>Вкладку добавили; иначе закрыли.</summary>
    public bool Added { get; set; }

    public int Index { get; set; }

    public WorkspaceState? Tab { get; set; }

    public string? PersonalId { get; set; }

    public WorkspaceState? Personal { get; set; }

    public int PersonalIndex { get; set; }

    public string? OwnerId { get; set; }

    public bool OwnerEnabled { get; set; }
}

/// <summary>Запись журнала на диске — UserData\journal.json.</summary>
public sealed class JournalEntryState
{
    public DateTime Time { get; set; }

    public LogLevel Level { get; set; }

    public string Text { get; set; } = string.Empty;

    public string? Scope { get; set; }

    public AppSection? Section { get; set; }

    public string? Tab { get; set; }

    public bool IsServerLine { get; set; }

    public JournalOutputMark? Output { get; set; }
}
