using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using MultiServerSync.Core;
using MultiServerSync.Services;

namespace MultiServerSync.Views.Controls;

public partial class LogPanel : UserControl
{
    private readonly ListCollectionView _view = new(AppLog.Entries);

    public LogPanel()
    {
        InitializeComponent();

        LogList.ItemsSource = _view;
        AppLog.Written += OnWritten;
        Unloaded += (_, _) => AppLog.Written -= OnWritten;
    }

    /// <summary>То же, что у терминала: строка кнопок — в одной строке с ярлыками «Терминал / Журнал».</summary>
    public void AlignToolbar(double indent, double height)
    {
        ToolBar.Margin = new Thickness(indent, 0, 0, 4);
        ToolBar.MinHeight = height;
    }

    private void OnWritten(LogEntry entry)
    {
        if (AutoScrollBox.IsChecked != true || entry.IsHint)
        {
            return;
        }

        // свежая запись всегда первая — держим начало списка в поле зрения
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_view.Count > 0)
            {
                LogList.ScrollIntoView(_view.GetItemAt(0));
            }
        }));
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        var box = (ComboBox)sender;
        var section = (box.SelectedItem as ComboBoxItem)?.Content as string;

        _view.Filter = section is null || box.SelectedIndex == 0
            ? null
            : item => item is LogEntry entry && entry.SectionText == section;
    }

    private void OnClear(object sender, RoutedEventArgs e) => AppLog.Clear();

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(AppLog.ExportText());
            AppLog.Hint("Журнал скопирован в буфер обмена.");
        }
        catch (Exception ex)
        {
            AppLog.Hint("Не удалось скопировать журнал: " + ex.Message, LogLevel.Error);
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(AppPaths.LogsDir) { UseShellExecute = true });
    }
}
