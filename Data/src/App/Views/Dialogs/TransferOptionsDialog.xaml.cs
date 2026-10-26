using System.Windows;
using MultiServerSync.Services;
using Wpf.Ui.Controls;

namespace MultiServerSync.Views.Dialogs;

/// <summary>
/// Параметры копирования вкладки «Проводника» отдельным окном: на странице всё место
/// отдано панелям. Исключения — здесь же, кнопкой в шапке блока.
/// </summary>
public partial class TransferOptionsDialog : FluentWindow
{
    private TransferOptionsDialog(Workspace tab)
    {
        InitializeComponent();
        Options.DataContext = tab;
    }

    public static void Open(Window? owner, Workspace tab)
    {
        var dialog = new TransferOptionsDialog(tab) { Owner = owner };
        dialog.ShowDialog();
        AppState.Current.SaveWorkspaces();
    }

    private void OnClose(object sender, RoutedEventArgs e) => DialogResult = true;
}
