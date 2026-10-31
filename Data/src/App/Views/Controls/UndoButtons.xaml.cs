using System.Windows;
using System.Windows.Controls;
using MultiServerSync.Services;

namespace MultiServerSync.Views.Controls;

/// <summary>Стрелки показывают память открытой вкладки и что именно они вернут; делать нечего — тусклые.</summary>
public partial class UndoButtons : UserControl
{
    public UndoButtons()
    {
        InitializeComponent();

        var state = AppState.Current;

        UndoJournal.Changed += Show;
        state.ActiveWorkspaceChanged += _ => Show();
        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppState.Section) or nameof(AppState.ActiveServerSet))
            {
                Show();
            }
        };

        Show();
    }

    private void Show()
    {
        var undo = UndoJournal.UndoName();
        var redo = UndoJournal.RedoName();

        UndoButton.IsEnabled = undo is not null;
        RedoButton.IsEnabled = redo is not null;
        UndoButton.ToolTip = undo is null ? "Отменять нечего" : $"Отменить: {undo} (Ctrl+Z)";
        RedoButton.ToolTip = redo is null ? "Возвращать нечего" : $"Вернуть: {redo} (Ctrl+Y)";
    }

    private void OnUndo(object sender, RoutedEventArgs e) => UndoJournal.Undo();

    private void OnRedo(object sender, RoutedEventArgs e) => UndoJournal.Redo();
}
