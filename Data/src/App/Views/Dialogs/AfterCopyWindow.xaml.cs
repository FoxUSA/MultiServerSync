using System.Windows;
using System.Windows.Input;
using MultiServerSync.Services;
using MultiServerSync.Views.Pages;
using Wpf.Ui.Controls;

namespace MultiServerSync.Views.Dialogs;

/// <summary>
/// Команды после копирования — страница «Команды» личной вкладки в окне поверх программы, а не переходом
/// в раздел. Закрыли, ничего не написав в только что созданной вкладке, — она убирается, как при уходе.
/// </summary>
public partial class AfterCopyWindow : FluentWindow
{
    private AfterCopyWindow(Workspace personal)
    {
        InitializeComponent();

        Title = $"Команды после копирования · «{personal.Title}»";

        var settings = AppState.Current.Settings;
        Width = Math.Max(MinWidth, settings.AfterCopyWidth);
        Height = Math.Max(MinHeight, settings.AfterCopyHeight);

        PageHost.Navigate(new CommandPage(personal));

        InputBindings.Add(new KeyBinding(new Core.RelayCommand(_ => Close()), Key.Escape, ModifierKeys.None));

        Closing += (_, _) =>
        {
            if (WindowState == WindowState.Normal)
            {
                settings.AfterCopyWidth = ActualWidth;
                settings.AfterCopyHeight = ActualHeight;
            }
        };
    }

    /// <summary>Открытые окна: у личной вкладки одно, повторное открытие поднимает его.</summary>
    private static readonly Dictionary<Workspace, AfterCopyWindow> Opened = new();

    /// <summary>
    /// Окно личной вкладки поверх программы, но не модальное: главное окно остаётся в работе —
    /// видно терминал, можно отмечать серверы и запускать копирование.
    /// </summary>
    public static void Open(Workspace personal)
    {
        if (Opened.TryGetValue(personal, out var open))
        {
            if (open.WindowState == WindowState.Minimized)
            {
                open.WindowState = WindowState.Normal;
            }

            open.Activate();
            return;
        }

        var window = new AfterCopyWindow(personal) { Owner = Application.Current.MainWindow };
        Opened[personal] = window;
        AppState.Current.PersonalWindowOpened(personal);

        window.Closed += (_, _) =>
        {
            Opened.Remove(personal);
            AppState.Current.PersonalWindowClosed(personal);
            AppState.Current.SaveWorkspaces();
        };

        window.Show();
    }
}
