using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace MultiServerSync.Views.Dialogs;

/// <summary>
/// Список запусков терминала в отдельном окне — например, на втором мониторе. Таблица одна:
/// её переносят сюда из главного окна, а при закрытии она возвращается на место. Выбор строки
/// здесь — тот же выбор, что решает, чей вывод показан справа.
/// </summary>
public partial class RunsWindow : FluentWindow
{
    public RunsWindow(UIElement table)
    {
        InitializeComponent();

        InputBindings.Add(new KeyBinding(new Core.RelayCommand(_ => Close()), Key.Escape, ModifierKeys.None));

        Host.Child = table;

        // таблица уходит отсюда раньше, чем окно исчезнет: её ждут обратно в главном окне
        Closing += (_, _) => Host.Child = null;
    }
}
