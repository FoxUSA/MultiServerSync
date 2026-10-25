using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MultiServerSync.Models;
using MultiServerSync.Services;
using MultiServerSync.Views.Dialogs;

namespace MultiServerSync.Views.Pages;

/// <summary>
/// Серверы выбранного набора (вкладки этого раздела) — здесь их заводят, правят и удаляют.
/// Галочки в таблице нужны только для действий на этой странице: удалить, дублировать,
/// проверить связь. Куда копировать и где выполнять команду, отмечается в панели слева.
/// </summary>
public partial class ServersPage : Page
{
    public ServersPage()
    {
        InitializeComponent();

        DataContext = this;

        // порядок в таблице выбирается щелчком по заголовку и не сбрасывается
        GridSort.Keep(ServersGrid, "servers");

        // ярлыки наверху показывают наборы серверов, пока открыт этот раздел
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                AppState.Current.Section = AppSection.Servers;
            }
        };
    }

    /// <summary>Общее состояние: через него таблица смотрит на выбранный набор.</summary>
    public AppState State => AppState.Current;

    private ServerSet Set => AppState.Current.ActiveServerSet;

    /// <summary>
    /// С чем работают кнопки: отмеченные галочкой, а если галочек нет — выделенные строки.
    /// </summary>
    private IReadOnlyList<ServerEntry> Picked
    {
        get
        {
            var checkedOnes = Set.Picked;

            return checkedOnes.Count > 0
                ? checkedOnes
                : ServersGrid.SelectedItems.OfType<ServerCheck>().Select(c => c.Server).ToList();
        }
    }

    /// <summary>
    /// Клик по галочке в шапке. Состояние считаем сами, а не через двустороннюю привязку:
    /// трёхпозиционный CheckBox иначе на первом же клике из «частично» уходит не туда.
    /// </summary>
    private void OnToggleAll(object sender, RoutedEventArgs e) => Set.ToggleAll();

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var entry = new ServerEntry
        {
            Name = string.Empty,
            Host = string.Empty,
            Port = 22,
            User = "root",
            SetId = Set.Id,
        };

        if (!Edit(entry, isNew: true))
        {
            return;
        }

        AppState.Current.Servers.Add(entry);

        // новый сервер приходит без отметки — выбирает его сам человек; строку только показываем
        if (Set.Rows.FirstOrDefault(c => ReferenceEquals(c.Server, entry)) is { } row)
        {
            ServersGrid.SelectedItem = row;
            ServersGrid.ScrollIntoView(row);
        }

        AppState.Current.SaveServers();
        AppLog.Success($"Добавлен сервер в набор «{Set.Title}»: {entry.Display}");
    }

    /// <summary>Импорт из ~/.ssh/config, PuTTY и WinSCP — в открытый набор, без отметок.</summary>
    private void OnImport(object sender, RoutedEventArgs e)
    {
        var found = ServerImport.FindAll();

        if (found.Count == 0)
        {
            AppLog.Warn("Импортировать нечего: в ~/.ssh/config, PuTTY и WinSCP серверов не найдено.");
            return;
        }

        if (ImportServersDialog.Ask(Window.GetWindow(this), found, Set.Title) is not { Count: > 0 } picked)
        {
            return;
        }

        foreach (var c in picked)
        {
            AppState.Current.Servers.Add(new ServerEntry
            {
                Name = c.Name,
                Host = c.Host,
                Port = c.Port,
                User = c.User,
                KeyPath = c.KeyPath,
                SetId = Set.Id,
            });
        }

        AppState.Current.SaveServers();
        AppLog.Success($"Импортировано серверов в набор «{Set.Title}»: {picked.Count} ({string.Join(", ", picked.Select(c => c.Source).Distinct())}).");
    }

    private void OnEdit(object sender, RoutedEventArgs e) => EditSelected();

    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e) => EditSelected();

    private void EditSelected()
    {
        if (ServersGrid.SelectedItem is not ServerCheck { Server: { } entry })
        {
            return;
        }

        if (Edit(entry, isNew: false))
        {
            AppState.Current.SaveServers();
            AppLog.Info($"Изменён сервер: {entry.Display}");
        }
    }

    private bool Edit(ServerEntry entry, bool isNew)
    {
        var dialog = new ServerEditDialog(entry, isNew) { Owner = Window.GetWindow(this) };
        return dialog.ShowDialog() == true;
    }

    private void OnDuplicate(object sender, RoutedEventArgs e)
    {
        var picked = Picked;

        foreach (var s in picked)
        {
            var copy = s.Clone();
            copy.SetId = Set.Id;
            copy.Name = s.Name + " (копия)";
            AppState.Current.Servers.Add(copy);
        }

        if (picked.Count > 0)
        {
            AppState.Current.SaveServers();
            AppLog.Info($"Скопировано записей: {picked.Count}.");
        }
    }

    /// <summary>
    /// Копии отмеченных серверов уходят в выбранные наборы. Сервер с тем же адресом,
    /// портом и пользователем, что уже стоит в наборе, второй раз не добавляется.
    /// </summary>
    private void OnCopyToSets(object sender, RoutedEventArgs e)
    {
        var picked = Picked;

        if (picked.Count == 0)
        {
            AppLog.Warn("Скопировать в набор: не отмечено ни одного сервера.");
            AppState.Current.RequestLog();
            return;
        }

        var others = AppState.Current.ServerSets.Where(s => !ReferenceEquals(s, Set)).ToList();

        if (others.Count == 0)
        {
            AppLog.Warn("Скопировать в набор: других наборов нет — сначала добавьте вкладку плюсом слева вверху.");
            AppState.Current.RequestLog();
            return;
        }

        var targets = CopyToSetDialog.Ask(Window.GetWindow(this), picked.Count, others);

        if (targets.Count == 0)
        {
            return;
        }

        var added = 0;
        var skipped = 0;

        foreach (var target in targets)
        {
            foreach (var s in picked)
            {
                if (target.Servers.Any(t => SameServer(t, s)))
                {
                    skipped++;
                    continue;
                }

                var copy = s.Clone();
                copy.SetId = target.Id;
                copy.IsSelected = false;
                AppState.Current.Servers.Add(copy);
                added++;
            }
        }

        if (added > 0)
        {
            AppState.Current.SaveServers();
        }

        var where = string.Join(", ", targets.Select(t => $"«{t.Title}»"));
        var note = skipped > 0 ? $", уже были там: {skipped}" : string.Empty;
        var message = $"Скопировано серверов в {where}: {added}{note}.";

        if (added > 0)
        {
            AppLog.Success(message);
        }
        else
        {
            AppLog.Info(message);
        }
    }

    private static bool SameServer(ServerEntry a, ServerEntry b) =>
        string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port
        && string.Equals(a.User, b.User, StringComparison.Ordinal);

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        var picked = Picked;

        if (picked.Count == 0)
        {
            return;
        }

        var names = string.Join(", ", picked.Take(5).Select(s => s.PaneLabel))
            + (picked.Count > 5 ? $" и ещё {picked.Count - 5}" : string.Empty);

        if (!ConfirmDialog.Ask(
                Window.GetWindow(this),
                $"Удалить серверы ({picked.Count}) из набора «{Set.Title}»?",
                names,
                "Удалить"))
        {
            return;
        }

        foreach (var s in picked)
        {
            AppState.Current.Servers.Remove(s);
        }

        AppState.Current.SaveServers();
        AppLog.Warn($"Удалено записей: {picked.Count}.");
    }

    /// <summary>Проводник на выделенном (или первом отмеченном) сервере — своя вкладка или уже открытая.</summary>
    private void OnExplorer(object sender, RoutedEventArgs e)
    {
        var server = (ServersGrid.SelectedItem as ServerCheck)?.Server ?? Picked.FirstOrDefault();

        if (server is null)
        {
            AppLog.Warn("Выделите сервер в таблице — его папки откроются в «Проводнике».");
            AppState.Current.RequestLog();
            return;
        }

        var tab = AppState.Current.ExplorerFor(server);
        AppLog.Info($"Проводник: {server.PaneLabel}.", tab.Title);
        AppState.Current.ShowTab(tab);
    }

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        var picked = Picked;

        // результат уходит в терминал внизу окна — там же, где вывод команд и копирования,
        // а если проверять нечего, сама операция развернёт журнал с причиной
        await AppState.Current.ActiveWorkspace.RunCheckAsync(picked);
    }
}
