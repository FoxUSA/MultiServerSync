using MultiServerSync.Core;

namespace MultiServerSync.Models;

/// <summary>
/// Запись избранного: команда и понятное описание, зачем она нужна.
/// Живёт в settings.json, чтобы держать шпаргалку внутри программы, а не в отдельном файле.
/// Многозадачная запись хранит набор окон целиком и при вставке раскрывает их все.
/// </summary>
public sealed class CommandSnippet : ObservableObject
{
    private string _comment = string.Empty;
    private string _command = string.Empty;
    private List<SavedCommandStep> _steps = new();

    public string Comment
    {
        get => _comment;
        set => Set(ref _comment, value);
    }

    /// <summary>Команда одиночной записи; у многозадачной — текст первого окна.</summary>
    public string Command
    {
        get => _command;
        set
        {
            if (Set(ref _command, value))
            {
                Raise(nameof(Preview));
            }
        }
    }

    /// <summary>Окна многозадачной записи. Пусто — запись одиночная (и все записи старых версий).</summary>
    public List<SavedCommandStep> Steps
    {
        get => _steps;
        set
        {
            _steps = value ?? new();
            RaiseMany(nameof(Steps), nameof(IsMulti), nameof(MultiText), nameof(Preview));
        }
    }

    public bool IsMulti => _steps.Count > 1;

    /// <summary>Пометка в списке: сколько окон раскроет запись.</summary>
    public string MultiText => IsMulti ? $"{_steps.Count} окна" : string.Empty;

    /// <summary>Команда в одну строку — для списка: многострочные схлопываются, длинные обрезаются.</summary>
    public string Preview => IsMulti
        ? ShortLineConverter.Shorten(string.Join("  →  ", _steps.Select(s => s.Text.Trim())))
        : ShortLineConverter.Shorten(Command);

    /// <summary>Что вставить: окна многозадачной записи или одно окно с командой.</summary>
    public List<SavedCommandStep> AsSteps() => IsMulti
        ? _steps.Select(s => new SavedCommandStep { Text = s.Text, UseSudo = s.UseSudo }).ToList()
        : [new SavedCommandStep { Text = Command }];

    /// <summary>Записать набор окон: одно окно — одиночная запись, несколько — многозадачная.</summary>
    public void SetSteps(IReadOnlyList<SavedCommandStep> steps)
    {
        Command = steps.Count > 0 ? steps[0].Text : string.Empty;
        Steps = steps.Count > 1 ? steps.ToList() : new();
    }
}
