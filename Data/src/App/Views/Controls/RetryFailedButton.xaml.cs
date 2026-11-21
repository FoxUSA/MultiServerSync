using System.Windows;
using System.Windows.Controls;
using MultiServerSync.Services;

namespace MultiServerSync.Views.Controls;

/// <summary>
/// «Повторить упавшие» — у «Выполнить» в «Команде», «Копировании» и «Скачивании».
/// DataContext — вкладка; повторяет прогон, что показан в её терминале.
/// </summary>
public partial class RetryFailedButton : UserControl
{
    public RetryFailedButton()
    {
        InitializeComponent();
    }

    private async void OnRetry(object sender, RoutedEventArgs e)
    {
        if (DataContext is not Workspace { ResultsBatch: { } batch })
        {
            return;
        }

        await batch.RetryFailedAsync(AppState.Current.Settings.MaxParallel);

        if (AppState.Current.IsCommandBatch(batch) || AppState.Current.IsCopyBatch(batch))
        {
            AppState.Current.NotifyFinished(batch);
        }
    }
}
