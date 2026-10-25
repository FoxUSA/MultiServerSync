using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace MultiServerSync.Core;

/// <summary>
/// Минимальная база для привязок. Уведомления, пришедшие из фонового потока,
/// перекидываются в поток интерфейса — операции выполняются параллельно и пишут статусы откуда угодно.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null)
    {
        if (PropertyChanged is null)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            PropertyChanged.Invoke(this, new PropertyChangedEventArgs(name));
        }
        else
        {
            dispatcher.BeginInvoke(new Action(() =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name))));
        }
    }

    protected void RaiseMany(params string[] names)
    {
        foreach (var n in names)
        {
            Raise(n);
        }
    }
}
