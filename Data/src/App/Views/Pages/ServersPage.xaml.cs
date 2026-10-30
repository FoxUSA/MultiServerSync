using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MultiServerSync.Models;
using MultiServerSync.Services;
using MultiServerSync.Views.Dialogs;
using Wpf.Ui.Controls;

namespace MultiServerSync.Views.Pages;

/// <summary>
/// Наборы серверов — две таблицы рядом, как панели «Проводника». Щелчок выбирает таблицу
/// (она в рамке), ярлык наверху меняет её набор, кнопки действуют на неё. Галочки нужны
/// только для действий на этой странице: удалить, дублировать, перенести, проверить связь.
/// </summary>
public partial class ServersPage : Page, Controls.IToastHost
{
    public ServersPage()
    {
        InitializeComponent();

        DataContext = this;

        // порядок в таблицах выбирается щелчком по заголовку и не сбрасывается; колонки у них одни
        GridSort.Keep(LeftGrid, "servers");
        GridSort.Keep(RightGrid, "servers");

        AppState.Current.PropertyChanged += OnStateChanged;
        ShowPicked();

        // ярлыки наверху показывают наборы серверов, пока открыт этот раздел
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                AppState.Current.Section = AppSection.Servers;
            }
        };
    }

    /// <summary>Общее состояние: через него таблицы смотрят на свои наборы.</summary>
    public AppState State => AppState.Current;

    /// <summary>Набор выбранной таблицы.</summary>
    private ServerSet Set => AppState.Current.ActiveServerSet;

    private System.Windows.Controls.DataGrid PickedGrid => AppState.Current.ServersRightActive ? RightGrid : LeftGrid;

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppState.ServersRightActive))
        {
            ShowPicked();
        }
    }

    /// <summary>Рамка у выбранной таблицы, стрелка «Перенести» — в сторону другой.</summary>
    private void ShowPicked()
    {
        var right = AppState.Current.ServersRightActive;
        LeftFrame.Visibility = right ? Visibility.Collapsed : Visibility.Visible;
        RightFrame.Visibility = right ? Visibility.Visible : Visibility.Collapsed;
        MoveButton.Icon = new SymbolIcon(right ? SymbolRegular.ArrowLeft24 : SymbolRegular.ArrowRight24);
        CopyButton.Icon = new SymbolIcon(right ? SymbolRegular.ArrowLeft24 : SymbolRegular.ArrowRight24);
    }

    private void OnLeftPicked(object sender, RoutedEventArgs e) => AppState.Current.ServersRightActive = false;

    private void OnRightPicked(object sender, RoutedEventArgs e) => AppState.Current.ServersRightActive = true;

    /// <summary>
    /// С чем работают кнопки: отмеченные галочкой в выбранной таблице, а если галочек нет — выделенные строки.
    /// </summary>
    private IReadOnlyList<ServerEntry> Picked
    {
        get
        {
            var checkedOnes = Set.Picked;

            return checkedOnes.Count > 0
                ? checkedOnes
                : PickedGrid.SelectedItems.OfType<ServerCheck>().Select(c => c.Server).ToList();
        }
    }

    /// <summary>
    /// Клик по галочке в шапке. Состояние считаем сами, а не через двустороннюю привязку:
    /// трёхпозиционный CheckBox иначе на первом же клике из «частично» уходит не туда.
    /// </summary>
    private void OnToggleAllLeft(object sender, RoutedEventArgs e) => AppState.Current.ServersLeft.ToggleAll();

    private void OnToggleAllRight(object sender, RoutedEventArgs e) => AppState.Current.ServersRight.ToggleAll();

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var set = Set;
        var entry = new ServerEntry
        {
            Name = string.Empty,
            Host = string.Empty,
            Port = 22,
            User = "root",
            SetId = set.Id,
        };

        if (!Edit(entry, isNew: true))
        {
            return;
        }

        AppState.Current.Servers.Add(entry);

        // новый сервер приходит без отметки — выбирает его сам человек; строку только показываем
        if (set.Rows.FirstOrDefault(c => ReferenceEquals(c.Server, entry)) is { } row)
        {
            PickedGrid.SelectedItem = row;
            PickedGrid.ScrollIntoView(row);
        }

        AppState.Current.SaveServers();
        ActionLog.Success($"Добавлен сервер в набор «{set.Title}»: {entry.Display}", entry, AppSection.Servers, null,
            [$"пользователь: {entry.User}", $"адрес: {entry.Host}:{entry.Port}"]);
    }

    private void OnEdit(object sender, RoutedEventArgs e) => EditSelected(PickedGrid);

    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.DataGrid grid)
        {
            EditSelected(grid);
        }
    }

    private void EditSelected(System.Windows.Controls.DataGrid grid)
    {
        if (grid.SelectedItem is not ServerCheck { Server: { } entry })
        {
            return;
        }

        if (Edit(entry, isNew: false))
        {
            AppState.Current.SaveServers();
            ActionLog.Info($"Изменён сервер: {entry.Display}", entry, AppSection.Servers, null,
                [$"пользователь: {entry.User}", $"адрес: {entry.Host}:{entry.Port}"]);
        }
    }

    private bool Edit(ServerEntry entry, bool isNew)
    {
        var dialog = new ServerEditDialog(entry, isNew) { Owner = Window.GetWindow(this) };
        return dialog.ShowDialog() == true;
    }

    private void OnDuplicate(object sender, RoutedEventArgs e)
    {
        var set = Set;
        var picked = Picked;

        foreach (var s in picked)
        {
            var copy = s.Clone();
            copy.SetId = set.Id;
            copy.Name = s.Name + " (копия)";
            AppState.Current.Servers.Add(copy);
        }

        if (picked.Count > 0)
        {
            AppState.Current.SaveServers();
            ActionLog.Info($"Продублировано серверов в наборе «{set.Title}»: {picked.Count}", null, AppSection.Servers, null,
                picked.Select(s => $"{s.Display} → {s.Name} (копия)"));
        }
    }

    /// <summary>
    /// «Перенести» отправляет отмеченные серверы выбранной таблицы в набор другой, «Скопировать»
    /// кладёт туда копии, а оригиналы оставляет. Сервер с тем же адресом, портом и пользователем,
    /// что уже стоит в том наборе, второй раз не кладётся и остаётся на месте.
    /// </summary>
    private void OnMove(object sender, RoutedEventArgs e) => Transfer(keep: false);

    private void OnCopyOver(object sender, RoutedEventArgs e) => Transfer(keep: true);

    private void Transfer(bool keep)
    {
        var state = AppState.Current;
        var action = keep ? "Скопировать" : "Перенести";
        var from = Set;
        var to = state.ServersOther;
        var picked = Picked;

        if (picked.Count == 0)
        {
            AppLog.Hint($"{action}: не отмечено ни одного сервера.", LogLevel.Warning);
            return;
        }

        if (ReferenceEquals(from, to))
        {
            AppLog.Hint($"В обеих таблицах набор «{from.Title}» — откройте в другой таблице другой набор.", LogLevel.Warning);
            return;
        }

        var done = new List<ServerEntry>();
        var skipped = 0;

        foreach (var s in picked)
        {
            if (to.Servers.Any(t => SameServer(t, s)))
            {
                skipped++;
                continue;
            }

            if (keep)
            {
                var copy = s.Clone();
                copy.SetId = to.Id;
                copy.IsSelected = false;
                state.Servers.Add(copy);
            }
            else
            {
                s.SetId = to.Id;
            }

            done.Add(s);
        }

        if (done.Count > 0)
        {
            if (keep)
            {
                state.SaveServers();
            }
            else
            {
                state.ServersRegrouped();
            }
        }

        var note = skipped > 0 ? $", уже были там: {skipped}" : string.Empty;
        var verb = keep ? "Скопировано" : "Перенесено";

        ActionLog.Write(done.Count > 0 ? LogLevel.Success : LogLevel.Info,
            $"{verb} серверов из «{from.Title}» в «{to.Title}»: {done.Count}{note}.", null, AppSection.Servers, null,
            done.Select(s => s.Display));
    }

    private static bool SameServer(ServerEntry a, ServerEntry b) =>
        string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port
        && string.Equals(a.User, b.User, StringComparison.Ordinal);

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        var set = Set;
        var picked = Picked;

        if (picked.Count == 0)
        {
            return;
        }

        var names = string.Join(", ", picked.Take(5).Select(s => s.PaneLabel))
            + (picked.Count > 5 ? $" и ещё {picked.Count - 5}" : string.Empty);

        if (!ConfirmDialog.Ask(
                Window.GetWindow(this),
                $"Удалить серверы ({picked.Count}) из набора «{set.Title}»?",
                names,
                "Удалить",
                enterConfirms: true))
        {
            return;
        }

        foreach (var s in picked)
        {
            AppState.Current.Servers.Remove(s);
        }

        AppState.Current.SaveServers();
        ActionLog.Warn($"Удалено серверов из набора «{set.Title}»: {picked.Count}", null, AppSection.Servers, null,
            picked.Select(s => s.Display));
    }

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        var picked = Picked;

        // у раздела свой терминал: ответ проверки не попадает во вкладки копирования
        await AppState.Current.ServersDesk.RunCheckAsync(picked);
    }

    Controls.ActionToast Controls.IToastHost.PageToast => BarToast;
}
