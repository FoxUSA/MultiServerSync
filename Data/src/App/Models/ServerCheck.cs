using MultiServerSync.Core;

namespace MultiServerSync.Models;

/// <summary>
/// Отметка сервера внутри одной вкладки. Сам список серверов общий для всех вкладок,
/// а галочки у каждой вкладки свои — это и есть «свои условия» рабочего стола.
/// </summary>
public sealed class ServerCheck : ObservableObject
{
    private bool _isSelected;

    public ServerCheck(ServerEntry server, bool isSelected)
    {
        Server = server;
        _isSelected = isSelected;
    }

    public ServerEntry Server { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    private bool _isLocked;
    private bool _isMuted;

    /// <summary>Вкладка закреплена за этим сервером: закрытый замок в его строке виден всегда.</summary>
    public bool IsLocked
    {
        get => _isLocked;
        set => Set(ref _isLocked, value);
    }

    /// <summary>Вкладка закреплена за другим сервером: строка приглушена и не нажимается.</summary>
    public bool IsMuted
    {
        get => _isMuted;
        set => Set(ref _isMuted, value);
    }
}
