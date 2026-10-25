using System.Windows;
using System.Windows.Controls;
using MultiServerSync.Services;

namespace MultiServerSync.Views.Controls;

/// <summary>Полоса хода переноса: шаг, сколько передано и сколько осталось.</summary>
public partial class TransferStrip : UserControl
{
    public static readonly DependencyProperty BatchProperty = DependencyProperty.Register(
        nameof(Batch),
        typeof(OperationBatch),
        typeof(TransferStrip),
        new PropertyMetadata(null, (d, e) => ((TransferStrip)d).ShowStop(e.NewValue as OperationBatch)));

    public TransferStrip()
    {
        InitializeComponent();
    }

    /// <summary>Чей прогон останавливает кнопка на полосе; не задан — кнопки нет.</summary>
    public OperationBatch? Batch
    {
        get => (OperationBatch?)GetValue(BatchProperty);
        set => SetValue(BatchProperty, value);
    }

    private void ShowStop(OperationBatch? batch)
    {
        Stop.DataContext = batch;
        Stop.Visibility = batch is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
