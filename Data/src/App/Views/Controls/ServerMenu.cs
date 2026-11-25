using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using MultiServerSync.Models;
using MultiServerSync.Services;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace MultiServerSync.Views.Controls;

/// <summary>
/// Выбор сервера списком под кнопкой — один на «Проводник», окно обзора сервера и подпись
/// сервера в ряду разделов. У строки значок сервера, название, серым логин, справа адрес
/// моноширинным ровным столбцом; наборы — мелкими заголовками, как «НАБОРЫ СЕРВЕРОВ» в панели,
/// между ними черта. Выбранный — в тусклой оранжевой рамке, как отмеченный сервер слева.
/// </summary>
public static class ServerMenu
{
    /// <summary>
    /// Замки у строк — те же, что в панели целевых серверов: закрытый виден всегда, открытый — у
    /// подсвеченной строки; под замком вкладки остальные строки приглушены и открытых замков нет.
    /// </summary>
    public sealed record Locks(Func<bool> TabLocked, Func<ServerEntry, bool> IsLocked, Action<ServerEntry> Toggle)
    {
        /// <summary>Целевые серверы вкладки («Копирование», «Команда», «Скачивание») — как замки в панели слева.</summary>
        public static Locks For(Workspace tab) => new(() => tab.IsPaneLocked, tab.IsServerLocked, tab.TogglePaneLock);

        /// <summary>
        /// «Проводник»: сервер у вкладки один — закрепить другой значит сначала открыть его в правой панели.
        /// </summary>
        public static Locks ForExplorer(Workspace tab) => new(() => tab.IsPaneLocked, tab.IsServerLocked, server =>
        {
            if (!tab.IsPaneLocked && !ReferenceEquals(server, tab.ExplorerServer))
            {
                tab.PickExplorerServer(server);
            }

            tab.TogglePaneLock(server);
        });
    }

    public static void Show(FrameworkElement anchor, ServerEntry? current, Action<ServerEntry> pick, bool alignRight = false) =>
        Show(anchor, s => ReferenceEquals(s, current), pick, null, alignRight);

    /// <param name="isCurrent">Какие строки подсветить — у копирования и команды отмеченных бывает несколько.</param>
    /// <param name="locks">Замки в строках; нет — список без замков («Проводник», окно обзора).</param>
    /// <param name="alignRight">Правый край списка — вровень с правым краем кнопки.</param>
    /// <param name="muted">Все строки приглушены — «PowerShell» на «Команде»: сейчас работа не с серверами.</param>
    /// <param name="computer">
    /// «Проводник»: первой строкой «Этот компьютер» — открыть его в правой панели. Current — он сейчас открыт
    /// (тогда серверы не подсвечены), Pick — выбор.
    /// </param>
    /// <param name="marked">«Мониторинг»: у каких серверов справа значок «открыт в окне» — только пометка, выбору не мешает.</param>
    public static void Show(FrameworkElement anchor, Func<ServerEntry, bool> isCurrent, Action<ServerEntry> pick,
        Locks? locks = null, bool alignRight = false, bool muted = false, (bool Current, Action Pick)? computer = null,
        Func<ServerEntry, bool>? marked = null)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom, FontSize = 13 };

        // названия и адреса — одной шириной у всех строк: адреса и замки встают ровными столбцами
        Grid.SetIsSharedSizeScope(menu, true);

        if (computer is { } pc)
        {
            var item = new MenuItem();
            item.Header = ComputerRow(pc.Current, locks is not null);
            item.Click += (_, _) => pc.Pick();

            // под замком вкладка держит свой сервер — компьютер, как и прочие строки, приглушён
            item.Opacity = muted || locks?.TabLocked() == true ? 0.5 : 1;
            menu.Items.Add(item);
            menu.Items.Add(new Separator());

            if (pc.Current)
            {
                isCurrent = _ => false;
            }
        }

        var rows = new List<(MenuItem Item, ServerEntry Server)>();
        var sets = AppState.Current.ServerSets.Where(s => s.Rows.Count > 0).ToList();

        foreach (var set in sets)
        {
            if (sets.Count > 1)
            {
                if (menu.Items.Count > 0 && menu.Items[^1] is not Separator)
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
                var item = new MenuItem();
                item.Click += (_, _) => pick(server);
                menu.Items.Add(item);
                rows.Add((item, server));
            }
        }

        // замок меняет отметки и приглушение — строки перерисовываются, список остаётся открытым
        void Fill()
        {
            var tabLocked = locks?.TabLocked() == true;

            foreach (var (item, server) in rows)
            {
                var locked = locks?.IsLocked(server) == true;
                item.Header = Row(anchor, item, server, isCurrent(server), locks, tabLocked, locked, Fill, marked?.Invoke(server) == true);
                item.Opacity = muted || (tabLocked && !locked) ? 0.5 : 1;
            }
        }

        Fill();

        if (rows.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "Серверов нет — добавьте в разделе «Серверы»", IsEnabled = false });
        }

        if (alignRight)
        {
            // ширина списка известна только после раскладки: до сдвига он невидим, чтобы не прыгал
            menu.Opacity = 0;
            menu.Opened += (_, _) => menu.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                AlignRight(menu, anchor);
                menu.Opacity = 1;
            });
        }

        menu.IsOpen = true;
    }

    /// <summary>
    /// Сдвигает список так, чтобы правый край его карточки встал вровень с правым краем кнопки.
    /// Карточка — первая залитая рамка внутри: вокруг неё у окна списка поле под тень.
    /// </summary>
    private static void AlignRight(ContextMenu menu, FrameworkElement anchor)
    {
        if (PresentationSource.FromVisual(menu) is null || PresentationSource.FromVisual(anchor) is null)
        {
            return;
        }

        var card = Card(menu) ?? (FrameworkElement)menu;
        var cardRight = card.PointToScreen(new Point(card.ActualWidth, 0)).X;
        var anchorRight = anchor.PointToScreen(new Point(anchor.ActualWidth, 0)).X;
        var scale = VisualTreeHelper.GetDpi(anchor).DpiScaleX;

        menu.HorizontalOffset += (anchorRight - cardRight) / scale;
    }

    private static Border? Card(DependencyObject root)
    {
        var queue = new Queue<DependencyObject>([root]);

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();

            if (node is Border { Background: SolidColorBrush { Color.A: > 0 } } border)
            {
                return border;
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                queue.Enqueue(VisualTreeHelper.GetChild(node, i));
            }
        }

        return null;
    }

    /// <summary>Рамка строки: у выбранного — оранжевая плашка по именам темы, у прочих — прозрачная.</summary>
    private static Border Frame(UIElement child, bool current)
    {
        var frame = new Border
        {
            Child = child,
            Padding = new Thickness(6, 3, 8, 3),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
        };

        if (current)
        {
            frame.SetResourceReference(Border.BackgroundProperty, "Pick.Fill");
            frame.SetResourceReference(Border.BorderBrushProperty, "Pick.Stroke");
        }

        return frame;
    }

    /// <summary>Строка: значок, название и логин, адрес, замок. Рамка есть у всех, у выбранного — видимая.</summary>
    private static FrameworkElement Row(FrameworkElement anchor, MenuItem item, ServerEntry server, bool current,
        Locks? locks, bool tabLocked, bool locked, Action refill, bool marked = false)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "ServerName" });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "ServerAddress" });
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

        // только адрес: порт, даже нестандартный, в списке не нужен — он в подписи сервера и в его окне
        var address = new TextBlock
        {
            Text = server.Host,
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

        if (marked)
        {
            // мониторинг этого сервера открыт ещё в одном окне — значок там же, где у строки был бы замок
            var mark = new SymbolIcon
            {
                Symbol = SymbolRegular.Open24,
                FontSize = 14,
                Margin = new Thickness(14, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Открыт ещё в одном окне",
            };
            mark.SetResourceReference(Control.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
            Grid.SetColumn(mark, 3);
            grid.Children.Add(mark);
        }

        if (locks is not null)
        {
            // место под замок есть у каждой строки, даже без него: замки — ровным столбцом
            var slot = new Border { Width = 20, Height = 20, Margin = new Thickness(14, 0, 0, 0) };
            Grid.SetColumn(slot, 3);
            grid.Children.Add(slot);

            if (!tabLocked || locked)
            {
                slot.Child = LockButton(anchor, item, server, locks, locked, refill);
            }
        }

        return Frame(grid, current);
    }

    /// <summary>Строка «Этот компьютер» — тем же видом, что у сервера: значок, название, пустой адрес, место под замок.</summary>
    private static FrameworkElement ComputerRow(bool current, bool lockSlot)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "ServerName" });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "ServerAddress" });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Image
        {
            Source = ShellIcons.Computer,
            Width = 16,
            Height = 16,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var name = new TextBlock
        {
            Text = LocalSource.ComputerTitle,
            FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 20, 0),
        };

        Grid.SetColumn(name, 1);
        grid.Children.Add(icon);
        grid.Children.Add(name);

        if (lockSlot)
        {
            var slot = new Border { Width = 20, Height = 20, Margin = new Thickness(14, 0, 0, 0) };
            Grid.SetColumn(slot, 3);
            grid.Children.Add(slot);
        }

        return Frame(grid, current);
    }

    /// <summary>
    /// Замок строки — стиль замка из панели: под мышью залитый фон. В подсвеченной строке значок
    /// белый (основной цвет текста), открытый он или закрытый; вне её закрытый — цвета акцента,
    /// открытый спрятан.
    /// </summary>
    private static Button LockButton(FrameworkElement anchor, MenuItem item, ServerEntry server, Locks locks, bool locked, Action refill)
    {
        var button = new Button
        {
            Content = new SymbolIcon { Symbol = locked ? SymbolRegular.LockClosed24 : SymbolRegular.LockOpen24, FontSize = 13 },
            ToolTip = locked
                ? "Вкладка закреплена за этим сервером — щелчок снимает замок"
                : AppState.Current.ActiveWorkspace.LockTip,
        };

        if (anchor.TryFindResource("PaneLockButtonStyle") is Style style)
        {
            button.Style = style;
        }

        button.Margin = new Thickness(0);

        var highlighted = new Binding(nameof(MenuItem.IsHighlighted)) { Source = item };

        button.SetBinding(Control.ForegroundProperty, new Binding(highlighted.Path.Path)
        {
            Source = item,
            Converter = new Map(on => on
                ? Theme(anchor, "TextFillColorPrimaryBrush")
                : Theme(anchor, locked ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush")),
        });

        if (!locked)
        {
            button.SetBinding(UIElement.VisibilityProperty, new Binding(highlighted.Path.Path)
            {
                Source = item,
                Converter = new Map(on => on ? Visibility.Visible : Visibility.Hidden),
            });
        }

        // щелчок по замку не выбирает сервер и не закрывает список
        button.Click += (_, e) =>
        {
            e.Handled = true;
            locks.Toggle(server);
            button.Dispatcher.BeginInvoke(DispatcherPriority.Background, refill);
        };

        return button;
    }

    private static object Theme(FrameworkElement anchor, string key) =>
        anchor.TryFindResource(key) ?? Brushes.Gray;

    private sealed class Map(Func<bool, object> map) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => map(value is true);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
