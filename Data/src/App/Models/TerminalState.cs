using MultiServerSync.Services;

namespace MultiServerSync.Models;

/// <summary>Строка терминала на диске: сервер, итог и весь её вывод, с командами цепочки.</summary>
public sealed class TerminalRowState
{
    public string ServerId { get; set; } = string.Empty;

    public string Scope { get; set; } = string.Empty;

    public DateTime Created { get; set; }

    public OperationStatus Status { get; set; }

    public string Message { get; set; } = string.Empty;

    public double DurationMs { get; set; }

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

/// <summary>Запись журнала на диске — UserData\journal.json.</summary>
public sealed class JournalEntryState
{
    public DateTime Time { get; set; }

    public LogLevel Level { get; set; }

    public string Text { get; set; } = string.Empty;

    public string? Scope { get; set; }

    public AppSection? Section { get; set; }

    public bool IsServerLine { get; set; }
}
