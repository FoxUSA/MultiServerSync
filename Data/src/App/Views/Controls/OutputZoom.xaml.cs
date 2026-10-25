using System.Windows;
using System.Windows.Controls;
using MultiServerSync.Services;

namespace MultiServerSync.Views.Controls;

/// <summary>Лупа «−/+» над выводом: шаг — одна точка, размер общий и запоминается.</summary>
public partial class OutputZoom : UserControl
{
    public OutputZoom()
    {
        InitializeComponent();
    }

    private void OnSmaller(object sender, RoutedEventArgs e) => AppState.Current.ZoomOutput(-1);

    private void OnBigger(object sender, RoutedEventArgs e) => AppState.Current.ZoomOutput(+1);
}
