using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using MultiServerSync.Services;

namespace MultiServerSync.Views.Controls;

/// <summary>Срок жизни вывода терминала: «Хранить лог − 05:00 +». DataContext — показанный прогон.</summary>
public partial class OutputLife : UserControl
{
    private OperationBatch? _batch;

    public OutputLife()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Watch(e.NewValue as OperationBatch);
    }

    private void OnLess(object sender, RoutedEventArgs e) => Step(-1);

    private void OnMore(object sender, RoutedEventArgs e) => Step(+1);

    /// <summary>Новый срок — и у показанного терминала с выводом отсчёт идёт заново, без запуска.</summary>
    private void Step(int direction)
    {
        AppState.Current.StepOutputLife(direction);
        _batch?.RestartLife();
    }

    /// <summary>У вкладки стоит «Сохранять лог» — срок ей не нужен, окошко серое.</summary>
    private void Watch(OperationBatch? batch)
    {
        if (_batch is not null)
        {
            _batch.PropertyChanged -= OnBatchChanged;
        }

        _batch = batch;

        if (_batch is not null)
        {
            _batch.PropertyChanged += OnBatchChanged;
        }

        Apply();
    }

    private void OnBatchChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OperationBatch.KeepLog))
        {
            Apply();
        }
    }

    private void Apply() => Root.Opacity = _batch?.KeepLog == true ? 0.4 : 1;
}
