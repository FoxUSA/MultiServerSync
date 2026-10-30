using System.Windows;
using MultiServerSync.Services;

namespace MultiServerSync.Views.Controls;

/// <summary>
/// Рамка на кнопке раздела: что делается во вкладках раздела, пока человек в другом.
/// Те же цвета, что у ярлыков вкладок: зелёная мигает, пока идёт, красная — кончилось ошибкой.
/// </summary>
public static class SectionMark
{
    public static readonly DependencyProperty StatusProperty = DependencyProperty.RegisterAttached(
        "Status", typeof(WorkspaceStatus), typeof(SectionMark), new PropertyMetadata(WorkspaceStatus.None));

    public static WorkspaceStatus GetStatus(DependencyObject element) => (WorkspaceStatus)element.GetValue(StatusProperty);

    public static void SetStatus(DependencyObject element, WorkspaceStatus value) => element.SetValue(StatusProperty, value);
}
