using MultiServerSync.Core;

namespace MultiServerSync.Models;

/// <summary>
/// Запись избранного: команда и понятное описание, зачем она нужна.
/// Живёт в settings.json, чтобы держать шпаргалку внутри программы, а не в отдельном файле.
/// </summary>
public sealed class CommandSnippet : ObservableObject
{
    private string _comment = string.Empty;
    private string _command = string.Empty;

    public string Comment
    {
        get => _comment;
        set => Set(ref _comment, value);
    }

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

    /// <summary>Команда в одну строку — для списка: многострочные схлопываются, длинные обрезаются.</summary>
    public string Preview => ShortLineConverter.Shorten(Command);
}
