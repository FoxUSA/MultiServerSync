using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MultiServerSync.Models;

namespace MultiServerSync.Views.Controls;

/// <summary>
/// Протяжка мышью по строкам таблицы окна переворачивает их галочки: стояла — снимается, нет — ставится.
/// Галочки меняются только во время самой протяжки, начатой на строке: удаление строки и пересборка таблицы
/// тоже меняют выделение, и от этого ни одна галочка вставать не должна. Щелчок по одной строке галочку не трогает.
/// </summary>
public static class DragChecks
{
    public static void Attach(DataGrid grid)
    {
        // как стояли галочки при нажатии; пусто — протяжки нет
        Dictionary<IRowCheck, bool>? before = null;
        var dragging = false;

        void Finish()
        {
            var clear = dragging;

            before = null;
            dragging = false;

            // подсветка протяжки своё отработала: дальше видны только галочки
            if (clear)
            {
                grid.UnselectAll();
            }
        }

        grid.PreviewMouseLeftButtonDown += (_, e) =>
        {
            Finish();

            if (e.ClickCount == 1 && Keyboard.Modifiers == ModifierKeys.None && StartsOnRow(e.OriginalSource as DependencyObject))
            {
                before = grid.Items.OfType<IRowCheck>().ToDictionary(item => item, item => item.IsSelected);
            }
        };

        grid.SelectionChanged += (_, _) =>
        {
            if (before is null || Mouse.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            var picked = grid.SelectedItems.OfType<IRowCheck>().ToHashSet();

            // пока мышь на первой строке, это ещё щелчок
            if (!dragging && picked.Count < 2)
            {
                return;
            }

            dragging = true;

            foreach (var (item, was) in before)
            {
                item.IsSelected = was != picked.Contains(item);
            }
        };

        grid.PreviewMouseLeftButtonUp += (_, _) => Finish();
        grid.LostMouseCapture += (_, _) => Finish();
    }

    /// <summary>Нажали на строку, но не на её кнопки, галочку и не на шапку или полосу прокрутки.</summary>
    private static bool StartsOnRow(DependencyObject? node)
    {
        var onRow = false;

        while (node is not null)
        {
            if (node is ButtonBase or TextBox or ScrollBar or DataGridColumnHeader)
            {
                return false;
            }

            onRow |= node is DataGridRow;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return onRow;
    }
}
