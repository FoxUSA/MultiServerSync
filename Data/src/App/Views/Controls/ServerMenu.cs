using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MultiServerSync.Models;
using MultiServerSync.Services;

namespace MultiServerSync.Views.Controls;

/// <summary>
/// Выбор сервера списком под кнопкой — один на «Проводник» и окно обзора сервера.
/// Название слева, адрес справа ровным столбцом; наборов несколько — серверы под
/// названием своего набора.
/// </summary>
public static class ServerMenu
{
    public static void Show(FrameworkElement anchor, ServerEntry? current, Action<ServerEntry> pick)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        var sets = AppState.Current.ServerSets.Where(s => s.Rows.Count > 0).ToList();

        foreach (var set in sets)
        {
            if (sets.Count > 1)
            {
                menu.Items.Add(new MenuItem { Header = set.Title, IsEnabled = false, FontWeight = FontWeights.SemiBold });
            }

            foreach (var server in set.Servers)
            {
                var item = new MenuItem
                {
                    Header = string.IsNullOrWhiteSpace(server.User) ? server.PaneLabel : $"{server.PaneLabel} ({server.User})",

                    // столбец справа, где у пунктов меню пишут сочетания клавиш, — ровно под адрес
                    InputGestureText = server.Port is 22 or <= 0 ? server.Host : $"{server.Host}:{server.Port}",
                    IsChecked = ReferenceEquals(server, current),
                };
                item.Click += (_, _) => pick(server);
                menu.Items.Add(item);
            }
        }

        if (menu.Items.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "Серверов нет — добавьте в разделе «Серверы»", IsEnabled = false });
        }

        menu.IsOpen = true;
    }
}
