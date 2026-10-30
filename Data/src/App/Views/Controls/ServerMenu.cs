using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using MultiServerSync.Models;
using MultiServerSync.Services;

namespace MultiServerSync.Views.Controls;

/// <summary>
/// Выбор сервера списком под кнопкой — один на «Проводник», окно обзора сервера и подпись
/// сервера в ряду разделов. У строки значок сервера, название, серым логин, справа адрес
/// моноширинным ровным столбцом; наборы — мелкими заголовками, как «НАБОРЫ СЕРВЕРОВ» в панели,
/// между ними черта. Выбранный — в тусклой оранжевой рамке, как отмеченный сервер слева.
/// </summary>
public static class ServerMenu
{
    public static void Show(FrameworkElement anchor, ServerEntry? current, Action<ServerEntry> pick) =>
        Show(anchor, s => ReferenceEquals(s, current), pick);

    /// <param name="isCurrent">Какие строки подсветить — у копирования и команды отмеченных бывает несколько.</param>
    public static void Show(FrameworkElement anchor, Func<ServerEntry, bool> isCurrent, Action<ServerEntry> pick)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom, FontSize = 13 };

        // названия — одной шириной у всех строк: адреса встают ровным столбцом
        Grid.SetIsSharedSizeScope(menu, true);

        var sets = AppState.Current.ServerSets.Where(s => s.Rows.Count > 0).ToList();

        foreach (var set in sets)
        {
            if (sets.Count > 1)
            {
                if (menu.Items.Count > 0)
                {
                    menu.Items.Add(new Separator());
                }

                var title = new TextBlock
                {
                    Text = set.Title.ToUpperInvariant(),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(6, 2, 0, 0),
                };
                title.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

                // заголовок, а не пункт: мышь и клавиатура его не выбирают
                menu.Items.Add(new MenuItem { Header = title, IsHitTestVisible = false, Focusable = false });
            }

            foreach (var server in set.Servers)
            {
                var item = new MenuItem { Header = Row(server, isCurrent(server)) };
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

    private static readonly Brush PickFill = Frozen(0x26);
    private static readonly Brush PickStroke = Frozen(0x80);

    private static Brush Frozen(byte alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, 0xE0, 0x7A, 0x1F));
        brush.Freeze();
        return brush;
    }

    /// <summary>Строка: значок, название и логин, адрес. Рамка есть у всех, у выбранного — видимая.</summary>
    private static FrameworkElement Row(ServerEntry server, bool current)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "ServerName" });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Image
        {
            Source = ShellIcons.Server,
            Width = 16,
            Height = 16,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 0) };
        name.Inlines.Add(new Run(server.PaneLabel) { FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal });

        if (!string.IsNullOrWhiteSpace(server.User))
        {
            var user = new Run($"  ({server.User})");
            user.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorSecondaryBrush");
            name.Inlines.Add(user);
        }

        var address = new TextBlock
        {
            Text = server.Port is 22 or <= 0 ? server.Host : $"{server.Host}:{server.Port}",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        address.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");

        Grid.SetColumn(name, 1);
        Grid.SetColumn(address, 2);
        grid.Children.Add(icon);
        grid.Children.Add(name);
        grid.Children.Add(address);

        return new Border
        {
            Child = grid,
            Padding = new Thickness(6, 3, 8, 3),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Background = current ? PickFill : Brushes.Transparent,
            BorderBrush = current ? PickStroke : Brushes.Transparent,
        };
    }
}
