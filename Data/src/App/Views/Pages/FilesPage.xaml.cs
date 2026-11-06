using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MultiServerSync.Models;
using MultiServerSync.Services;
using MultiServerSync.Views.Dialogs;

namespace MultiServerSync.Views.Pages;

/// <summary>
/// Страница копирования. Вкладка делится на окна: у каждого свой список файлов, путь и параметры.
/// Таблица одна на все окна, строки окна — своей группой; панель кнопок и параметры показывают окно в фокусе,
/// фокус переходит щелчком по номеру окна. История путей на сервере общая для всех.
/// </summary>
public partial class FilesPage : Page
{
    private bool _activationHooked;

    /// <summary>Строки всех окон вкладки одним списком для общей таблицы.</summary>
    private readonly Controls.WindowRows _rows = new(downloads: false);

    public FilesPage()
    {
        InitializeComponent();

        DataContextChanged += (_, e) =>
        {
            RowsGrid.ItemsSource = _rows.Attach(Workspace);

            // номер окна в панели серверов — таблица показывает его строки
            if (e.OldValue is Workspace old)
            {
                old.WindowShowRequested -= ShowWindowRows;
            }

            if (e.NewValue is Workspace now)
            {
                now.WindowShowRequested += ShowWindowRows;
            }
        };
        DataContext = AppState.Current.ActiveWorkspace;
        AppState.Current.ActiveWorkspaceChanged += workspace =>
        {
            DataContext = workspace;

            // открыли другую вкладку — размер и файлы в её строках свежие
            if (IsVisible && AppState.Current.Workspaces.Contains(workspace))
            {
                _ = workspace.RefreshItemsInfoAsync();
            }
        };

        Controls.CopyWindowLayout.HookGroups(RowsGrid, _rows, ToggleWindowItems, FocusOn, AddWindowAfter, RemoveWindow, Rename);
        Controls.CopyWindowLayout.WatchFailures(this, () => Workspace);
        Controls.CopyWindowLayout.RestoreBand(BandRow, BandLeft);

        IsVisibleChanged += async (_, _) =>
        {
            if (!IsVisible)
            {
                return;
            }

            // ярлыки вкладок пишут статистику по открытому разделу
            AppState.Current.Section = AppSection.Files;

            if (Workspace is { } workspace)
            {
                await workspace.RefreshItemsInfoAsync();
            }
        };

        // файлы правят в других программах — при возврате в окно перечитываем размер и дату
        Loaded += (_, _) =>
        {
            if (_activationHooked || Window.GetWindow(this) is not { } owner)
            {
                return;
            }

            _activationHooked = true;

            owner.Activated += async (_, _) =>
            {
                if (IsVisible && Workspace is { } workspace)
                {
                    await workspace.RefreshItemsInfoAsync();
                }
            };
        };
    }

    private Workspace? Workspace => DataContext as Workspace;

    private void OnBandMoved(object sender, DragCompletedEventArgs e) => Controls.CopyWindowLayout.SaveBand(BandRow, BandLeft);

    /// <summary>Какое поле пути открыло свой список — крестик в списке чистит историю из него.</summary>
    private ComboBox? _openPathBox;

    private void ShowWindowRows(CopyWindow window) => Controls.CopyWindowLayout.Show(RowsGrid, _rows, window, focusRow: true);

    /// <summary>Окно элемента: строки пути, разделителя, строки таблицы или заглушки пустого окна.</summary>
    private CopyWindow? WindowOf(object? sender) =>
        _rows.WindowOf(sender is FrameworkElement element ? element.DataContext : sender);

    /// <summary>Щелчок по номеру окна в таблице — фокус на нём: кнопки ряда, строка пути, сводка и параметры показывают его.</summary>
    private void FocusOn(CopyWindow window)
    {
        Core.FocusDrop.Leave(this);
        Workspace?.FocusWindow(window);
    }

    /// <summary>Номер окна на разделителе: фокус на окне и все его строки отмечены — или, если уже все, сняты.</summary>
    private void ToggleWindowItems(CopyWindow window)
    {
        FocusOn(window);
        Workspace?.ToggleAllItems();
    }

    /// <summary>Плюс единственного окна — под таблицей.</summary>
    private void OnAddWindow(object sender, RoutedEventArgs e)
    {
        if (Workspace is { } workspace)
        {
            AddWindowAfter(workspace.FocusedWindow);
        }
    }

    private void AddWindowAfter(CopyWindow after)
    {
        if (Workspace is not { } workspace || workspace.BlockedByLock())
        {
            return;
        }

        Controls.CopyWindowLayout.Show(RowsGrid, _rows, workspace.AddCopyWindow(after));
    }

    private void RemoveWindow(CopyWindow window)
    {
        if (Workspace is { } workspace && !workspace.BlockedByLock())
        {
            workspace.RemoveCopyWindow(window);
        }
    }

    private void Rename(FrameworkElement plaque, CopyWindow window)
    {
        if (Workspace is { } workspace)
        {
            Controls.CopyWindowLayout.Rename(plaque, window, workspace);
        }
    }


    private void OnWindowGridLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is DataGrid grid)
        {
            Controls.CopyWindowLayout.Arrange(grid, "files");
        }
    }

    private void OnPathDropDownOpened(object? sender, EventArgs e) => _openPathBox = sender as ComboBox;

    /// <summary>
    /// Одно окно на всё: в нём выбираются и файлы, и папки, поодиночке или пачкой.
    /// Разделять их заранее двумя кнопками незачем — на сервер всё уходит одинаково.
    /// </summary>
    /// <summary>«Открыть». Под замком файлы можно посмотреть, но выбранное не добавляется: отказ плашкой.</summary>
    private void OnAdd(object sender, RoutedEventArgs e)
    {
        if (Workspace is not { } workspace)
        {
            return;
        }

        if (workspace.IsPaneLocked)
        {
            var owner = Window.GetWindow(this) is { } window ? new WindowInteropHelper(window).Handle : IntPtr.Zero;

            if (LocalPicker.Pick(owner, workspace.LastLocalFolder).Count > 0)
            {
                workspace.BlockedByLock();
            }

            return;
        }

        var picked = PickLocal(workspace, workspace.LastLocalFolder);

        if (picked.Count > 0)
        {
            workspace.AddPaths(picked);
        }
    }

    /// <summary>Двойной щелчок по пустому окну или его разделителю: тот же выбор, что у «Открыть», — в это окно, фокус не меняется.</summary>
    private void AddInto(CopyWindow window)
    {
        if (Workspace is not { } workspace)
        {
            return;
        }

        if (workspace.IsPaneLocked)
        {
            OnAdd(this, new RoutedEventArgs());
            return;
        }

        var picked = PickLocal(workspace, workspace.LastLocalFolder);

        if (picked.Count > 0)
        {
            workspace.AddPaths(picked, window);
        }
    }

    private IReadOnlyList<string> PickLocal(Workspace workspace, string? start)
    {
        var owner = Window.GetWindow(this) is { } window
            ? new WindowInteropHelper(window).Handle
            : IntPtr.Zero;

        var picked = LocalPicker.Pick(owner, start);

        if (picked.Count > 0)
        {
            // в следующий раз окно этой вкладки откроется там же, откуда добавляли сейчас
            workspace.LastLocalFolder = ParentOf(picked[0]);
        }

        return picked;
    }

    private static string ParentOf(string path) =>
        System.IO.Path.GetDirectoryName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar)) ?? string.Empty;

    /// <summary>
    /// Двойной щелчок: мимо строк — как «Открыть» для окна в фокусе, по пустому окну — в это окно, по строке окна
    /// над его строками — «Обзор» пути этого окна, по строке — другой адрес для неё.
    /// </summary>
    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e) =>
        Controls.CopyWindowLayout.DoubleClick(e, () => OnAdd(sender, e), row =>
        {
            if (row is TransferItem item)
            {
                PickForRow(item);
            }
            else if (row is System.Windows.Data.CollectionViewGroup && WindowOf(row) is { } browsed)
            {
                // строка окна над его строками — «Обзор» этого окна: первый щелчок уже дал ему фокус
                OnBrowse(browsed, e);
            }
            else if (WindowOf(row) is { } window)
            {
                AddInto(window);
            }
        });

    /// <summary>Тот же выбор, что у «Открыть», с папки этой строки: первое выбранное встаёт на её место, остальное — новыми строками.</summary>
    private void PickForRow(TransferItem item)
    {
        if (Workspace is not { } workspace || workspace.BlockedByLock())
        {
            return;
        }

        var picked = PickLocal(workspace, ParentOf(item.FullPath));

        if (picked.Count > 0)
        {
            var window = WindowOf(item);
            ReplaceRow(workspace, item, picked[0]);
            workspace.AddPaths(picked.Skip(1), window);
        }
    }

    /// <summary>Под замком исключения открываются только посмотреть.</summary>
    private void OnExcludes(object sender, RoutedEventArgs e)
    {
        if (Workspace is { } workspace)
        {
            ExcludeDialog.Edit(Window.GetWindow(this), workspace, "копировании", viewOnly: workspace.IsPaneLocked);
        }
    }

    /// <summary>Значок в конце строки: путь строки — в буфер.</summary>
    private void OnCopyItemPath(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TransferItem item } && ClipboardText.Copy(item.FullPath))
        {
            AppLog.Echo("Путь скопирован: " + item.FullPath);
        }
    }

    /// <summary>Значок в поле «Путь на сервере»: путь окна — в буфер обмена.</summary>
    private void OnCopyRemotePath(object sender, RoutedEventArgs e)
    {
        var path = WindowOf(sender)?.RemotePath?.Trim();

        if (string.IsNullOrEmpty(path))
        {
            AppLog.Hint("Путь на сервере пустой — копировать нечего.", LogLevel.Warning);
            return;
        }

        if (ClipboardText.Copy(path))
        {
            AppLog.Echo("Путь скопирован: " + path);
        }
    }

    /// <summary>Значок в конце строки: вписать другой путь — строка встаёт на то же место.</summary>
    private void OnEditItemPath(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TransferItem item } || Workspace is not { } workspace || workspace.BlockedByLock())
        {
            return;
        }

        var dialog = new InputDialog("Путь строки", "Файл или папка на компьютере:", item.FullPath) { Owner = Window.GetWindow(this) };

        if (dialog.ShowDialog() == true && dialog.Value.Length > 0)
        {
            ReplaceRow(workspace, item, dialog.Value);
        }
    }

    /// <summary>Правка списка в журнал не идёт.</summary>
    private static void ReplaceRow(Workspace workspace, TransferItem item, string path)
    {
        if (string.Equals(path, item.FullPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (workspace.ReplaceItemPath(item, path) is { } problem)
        {
            AppLog.Hint("Путь не сменён: " + problem + ".", LogLevel.Warning);
        }
    }

    /// <summary>Корзина в конце строки: строка уходит из списка, файл на месте.</summary>
    private void OnRemoveItemRow(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TransferItem item } && Workspace is { } workspace && !workspace.BlockedByLock())
        {
            workspace.RemoveItems([item]);
        }
    }

    private void OnClearItems(object sender, RoutedEventArgs e)
    {
        if (Workspace is not { Items.Count: > 0 } workspace || workspace.BlockedByLock())
        {
            return;
        }

        // отмеченные, а без отметок — все строки окна
        var picked = workspace.Items.Where(i => i.IsSelected).ToList();
        var gone = picked.Count > 0 ? picked : workspace.Items.ToList();
        workspace.RemoveItems(gone);
        ActionLog.Info(workspace.Items.Count == 0
            ? $"Список копирования очищен: убрано {gone.Count}"
            : $"Из списка копирования убрано: {gone.Count} · осталось {workspace.Items.Count}",
            null, AppSection.Files, workspace, gone.Select(i => i.FullPath).ToList());
    }

    /// <summary>Галочка общей шапки — строки всех окон.</summary>
    private void OnToggleAllItems(object sender, RoutedEventArgs e) => Workspace?.ToggleAllWindowsItems();

    /// <summary>«Разные сервера» / «Один сервер»: свои целевые серверы у каждого окна или общие.</summary>
    private void OnSeparateServers(object sender, RoutedEventArgs e) => Workspace?.ToggleSeparateServers();

    private async void OnRefreshItems(object sender, RoutedEventArgs e)
    {
        if (Workspace is { } workspace)
        {
            await workspace.RefreshItemsInfoAsync();
        }
    }

    /// <summary>Крестик в выпадающем списке: убирает один путь из общей истории.</summary>
    private void OnRemoveRemotePath(object sender, RoutedEventArgs e)
    {
        // иначе щелчок дойдёт до строки списка и путь будет выбран
        e.Handled = true;

        if (sender is not FrameworkElement { DataContext: string value })
        {
            return;
        }

        var settings = AppState.Current.Settings;

        if (Workspace is not { } workspace)
        {
            return;
        }

        // удаление выбранной строки очищает поля ввода — возвращаем то, что там было, у всех окон
        var paths = workspace.CopyWindows.Select(w => (Window: w, w.RemotePath)).ToList();

        if (!settings.RemotePathHistory.Remove(value))
        {
            return;
        }

        AppState.Current.SaveSettings();
        ActionLog.Info($"Из истории путей на сервере удалено: {value}", null, AppSection.Files, workspace);

        Dispatcher.BeginInvoke(new Action(() =>
        {
            foreach (var (window, path) in paths)
            {
                window.RemotePath = path;
            }
        }));

        if (_openPathBox is { } box)
        {
            box.IsDropDownOpen = settings.RemotePathHistory.Count > 0;
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && Workspace?.IsPaneLocked != true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || Workspace is not { } workspace || workspace.BlockedByLock())
        {
            return;
        }

        // бросили на строку окна, его разделитель или строку пути — файлы в это окно, фокус не меняется;
        // мимо окон — в то, что в фокусе
        CopyWindow? target = null;

        for (var node = e.OriginalSource as DependencyObject; node is not null && target is null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement element)
            {
                target = WindowOf(element.DataContext);
            }
        }

        workspace.AddPaths(paths, target);
    }

    /// <summary>Щелчок по плашке — туда, где устраняется: в личную вкладку команд.</summary>
    private void OnNoticeClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Workspace?.Personal is { } personal)
        {
            AppState.Current.JumpToPersonal(personal);
            AppState.Current.ShowTab(personal);
        }
    }

    /// <summary>«Обзор» в строке пути окна. Под замком открывается только посмотреть: путь окна не меняется.</summary>
    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        if (Workspace is not { } workspace)
        {
            return;
        }

        var window = WindowOf(sender) ?? workspace.FocusedWindow;

        var settings = AppState.Current.Settings;

        // обзор открывается на отмеченном слева сервере, из нескольких — на первом в списке;
        // ничего не отмечено — там, где выбирали в прошлый раз
        var browseServer =
            workspace.CopyTargets.FirstOrDefault()
            ?? AppState.Current.Servers.FirstOrDefault(s => s.Id == settings.BrowseServerId)
            ?? AppState.Current.Servers.FirstOrDefault();

        if (browseServer is null)
        {
            AppLog.Hint("Сначала добавьте хотя бы один сервер.", LogLevel.Warning);
            return;
        }

        var locked = workspace.IsPaneLocked;
        var dialog = new RemoteBrowserWindow(browseServer, SshService.NormalizeRemote(window.RemotePath), viewOnly: locked)
        {
            Owner = Window.GetWindow(this),
        };

        if (dialog.ShowDialog() == true && !locked && dialog.SelectedPath is { } picked)
        {
            window.RemotePath = picked;
            settings.BrowseServerId = dialog.Server.Id;
            AppState.Current.SaveSettings();
        }
    }

    /// <summary>«Листинг»: ls -la пути окна на его серверах — в журнал. На серверах ничего не меняет, замок не мешает.</summary>
    private async void OnListing(object sender, RoutedEventArgs e)
    {
        if (Workspace is not { } workspace)
        {
            return;
        }

        var path = (WindowOf(sender) ?? workspace.FocusedWindow).RemotePath?.Trim() ?? string.Empty;

        if (path.Length > 0 && workspace.CopyTargets.Count == 0 && Window.GetWindow(this) is MainWindow main)
        {
            main.FlashTargets();
        }

        await workspace.RunListingAsync(workspace.CopyTargets, path.Length == 0 ? [] : [SshService.NormalizeRemote(path)]);
    }

    /// <summary>«Выполнить»: эта вкладка — все её окна и серверы; со свитчем «Везде» — все вкладки.</summary>
    private async void OnCopy(object sender, RoutedEventArgs e)
    {
        if (Workspace is not { } workspace)
        {
            return;
        }

        if (AppState.Current.Everywhere)
        {
            await CopyEverywhereAsync(workspace);
        }
        else
        {
            await workspace.RunCopyAsync();
        }
    }

    /// <summary>
    /// «Сравнить»: файлы вкладки против сервера по правилам из параметров, без копирования.
    /// Со свитчем «Везде» — на всех вкладках сразу и без вопроса: на серверах ничего не меняется.
    /// </summary>
    private async void OnCompare(object sender, RoutedEventArgs e)
    {
        if (Workspace is not { } workspace)
        {
            return;
        }

        var all = AppState.Current.Everywhere ? AppState.Current.Workspaces.ToList() : [workspace];

        if (all.Count <= 1)
        {
            await workspace.RunCompareCopyAsync();
            return;
        }

        AppLog.Info($"Сравнение на всех вкладках: {all.Count}.");

        var running = all.Select(w => w.RunCompareCopyAsync(notify: false)).ToList();
        var started = all.Where((_, i) => !(running[i].IsCompletedSuccessfully && !running[i].Result)).ToList();

        if (started.Count == 0)
        {
            AppLog.Warn("Сравнение не начато ни на одной вкладке — причины выше.");
            await Task.WhenAll(running);
            return;
        }

        workspace.ShowSummary(started.Select(w => w.CopyBatch).ToList(), Services.Workspace.CompareName);

        await Task.WhenAll(running);

        AppState.Current.NotifyFinished(workspace.SummaryBatch);
    }

    /// <summary>
    /// «Выполнить» со свитчем «Везде» — копирование запускается во всех вкладках сразу, каждая со своими
    /// условиями. Сводка показывается на той вкладке, с которой нажали.
    /// </summary>
    private async Task CopyEverywhereAsync(Workspace workspace)
    {
        var all = AppState.Current.Workspaces.ToList();

        // широкое действие: копирование уходит со всех вкладок сразу — сначала спросить
        if (!ConfirmDialog.Ask(
                Window.GetWindow(this),
                $"Запустить копирование на всех вкладках ({all.Count})?",
                string.Empty,
                "Копировать везде"))
        {
            AppLog.Hint("Запуск на всех вкладках отменён.");
            return;
        }

        // умный режим — одним окном на все вкладки, а не по окну на каждую
        var checks = all.Select(t => (Tab: t, Check: t.PrecheckCopy())).ToList();

        if (RunHistory.ConfirmMany(checks) is not { } chosen)
        {
            return;
        }

        foreach (var (tab, check) in checks.Where(c => chosen.Contains(c.Tab)))
        {
            RunHistory.Commit(check);
        }

        if (chosen.Count == 0)
        {
            AppLog.Info("Запуск на всех вкладках: запускать нечего — все вкладки пропущены.");
            return;
        }

        if (chosen.Count == 1)
        {
            await chosen[0].RunCopyAsync(approved: true);
            return;
        }

        AppLog.Info($"Везде: копирование запускается во всех вкладках: {chosen.Count}.");

        // операции стартуют синхронно, поэтому строки уже созданы и попадут в сводку
        var running = chosen.Select(w => w.RunCopyAsync(notify: false, approved: true)).ToList();

        // вкладка, которая отказалась (нет файлов, пути или серверов), в сводку не идёт:
        // её отказ уже виден на ярлыке и в журнале
        var started = chosen.Where((_, i) => !(running[i].IsCompletedSuccessfully && !running[i].Result)).ToList();

        if (started.Count == 0)
        {
            AppLog.Warn("Копирование не начато ни на одной вкладке — причины выше.");
            await Task.WhenAll(running);
            return;
        }

        // сводка только в терминале: на ярлыке первой вкладки — её собственная работа
        workspace.ShowSummary(started.Select(w => w.CopyBatch).ToList(), "копирование");

        await Task.WhenAll(running);

        // автокопия — по сводке, когда закончили все вкладки, а не по первой закончившей
        AppState.Current.NotifyFinished(workspace.SummaryBatch);
    }

}
