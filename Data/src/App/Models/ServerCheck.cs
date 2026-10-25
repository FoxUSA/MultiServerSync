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
}
