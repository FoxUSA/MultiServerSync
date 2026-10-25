using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using MultiServerSync.Core;
using MultiServerSync.Services;

namespace MultiServerSync.Views.Controls;

public partial class LogPanel : UserControl
{
    public LogPanel()
    {
        InitializeComponent();

        LogList.ItemsSource = AppLog.Entries;
        AppLog.Written += OnWritten;
        Unloaded += (_, _) => AppLog.Written -= OnWritten;
    }

    private void OnWritten(LogEntry entry)
    {
        if (AutoScrollBox.IsChecked != true)
        {
            return;
        }

        // свежая запись всегда первая — держим начало списка в поле зрения
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (AppLog.Entries.Count > 0)
            {
                LogList.ScrollIntoView(AppLog.Entries[0]);
            }
        }));
    }

    private void OnClear(object sender, RoutedEventArgs e) => AppLog.Clear();

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(AppLog.ExportText());
            AppLog.Info("Журнал скопирован в буфер обмена.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось скопировать журнал: " + ex.Message);
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(AppPaths.LogsDir) { UseShellExecute = true });
    }
}
