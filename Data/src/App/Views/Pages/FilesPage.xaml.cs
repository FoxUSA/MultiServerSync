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
/// Панель кнопок и параметры показывают окно в фокусе, история путей на сервере общая для всех.
/// </summary>
public partial class FilesPage : Page
{
    private bool _activationHooked;

    public FilesPage()
    {
        InitializeComponent();

        DataContext = AppState.Current.ActiveWorkspace;
        AppState.Current.ActiveWorkspaceChanged += workspace => DataContext = workspace;

        Controls.CopyWindowLayout.Follow(WindowsScroll, WindowsList);
        Controls.CopyWindowLayout.Reorder(WindowsScroll, WindowsList, () => Workspace);
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

    /// <summary>Щелчок в окне переводит на него панель и параметры — раньше, чем сработает его кнопка.</summary>
    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        Core.FocusDrop.Leave(sender);
        FocusWindowOf(sender);
    }

    private void OnWindowKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => FocusWindowOf(sender);

    private void FocusWindowOf(object sender)
    {
        if (sender is FrameworkElement { DataContext: CopyWindow window })
        {
            Workspace?.FocusWindow(window);
        }
    }

    private void OnAddWindow(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CopyWindow after } || Workspace is not { } workspace || workspace.BlockedByLock())
        {
            return;
        }

        Controls.CopyWindowLayout.Show(WindowsList, workspace.AddCopyWindow(after));
    }

    private void OnRemoveWindow(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CopyWindow window } && Workspace is { } workspace && !workspace.BlockedByLock())
        {
            workspace.RemoveCopyWindow(window);
        }
    }

    /// <summary>Кнопка «Название»: плашки названий в окнах вкладки. Замок не держит: без плашек названия не сменить.</summary>
    private void OnWindowNames(object sender, RoutedEventArgs e)
    {
        if (Workspace is { } workspace)
        {
            workspace.ShowWindowNames = !workspace.ShowWindowNames;
        }
    }

    private void OnWindowNameDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement plaque && Workspace is { } workspace)
        {
            e.Handled = true;
            Controls.CopyWindowLayout.Rename(plaque, workspace);
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
    private void OnAdd(object sender, RoutedEventArgs e)
    {
        if (Workspace is not { } workspace || workspace.BlockedByLock())
        {
            return;
        }

        var picked = PickLocal(workspace, workspace.LastLocalFolder);

        if (picked.Count > 0)
        {
            workspace.AddPaths(picked);
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

    /// <summary>Двойной щелчок: мимо строк — как «Открыть» для этого окна, по строке — другой адрес для неё.</summary>
    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e) =>
        Controls.CopyWindowLayout.DoubleClick(e, () => OnAdd(sender, e), row => PickForRow((TransferItem)row));

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
            ReplaceRow(workspace, item, picked[0]);
            workspace.AddPaths(picked.Skip(1));
        }
    }

    private void OnExcludes(object sender, RoutedEventArgs e)
    {
        if (Workspace is { } workspace && !workspace.BlockedByLock())
        {
            ExcludeDialog.Edit(Window.GetWindow(this), workspace, "копировании");
        }
    }

    /// <summary>Значок в конце строки: путь строки — в буфер.</summary>
    private void OnCopyItemPath(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TransferItem item } && ClipboardText.Copy(item.FullPath))
        {
            AppLog.Hint("Путь скопирован: " + item.FullPath);
        }
    }

    /// <summary>Значок в поле «Путь на сервере»: путь окна — в буфер обмена.</summary>
    private void OnCopyRemotePath(object sender, RoutedEventArgs e)
    {
        var path = Workspace?.RemotePath?.Trim();

        if (string.IsNullOrEmpty(path))
        {
            AppLog.Hint("Путь на сервере пустой — копировать нечего.", LogLevel.Warning);
            return;
        }

        if (ClipboardText.Copy(path))
        {
            AppLog.Hint("Путь скопирован: " + path);
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

    /// <summary>Тот же адрес — ничего не меняется и в журнал не идёт.</summary>
    private static void ReplaceRow(Workspace workspace, TransferItem item, string path)
    {
        if (string.Equals(path, item.FullPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (workspace.ReplaceItemPath(item, path) is { } problem)
        {
            AppLog.Hint("Путь не сменён: " + problem + ".", LogLevel.Warning);
            return;
        }

        ActionLog.Info($"Путь строки сменён: {item.Name} → {System.IO.Path.GetFileName(path.TrimEnd('\\'))}",
            ActionLog.Computer, AppSection.Files, workspace, [$"было: {item.FullPath}", $"стало: {path}"]);
    }

    /// <summary>Корзина в конце строки: строка уходит из списка, файл на месте.</summary>
    private void OnRemoveItemRow(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TransferItem item } && Workspace is { } workspace && !workspace.BlockedByLock())
        {
            workspace.RemoveItems([item]);
            ActionLog.Info($"Убрана строка: {item.Name}", null, AppSection.Files, workspace,
                [item.FullPath, $"в списке осталось: {workspace.Items.Count}"]);
        }
    }

    private void OnClearItems(object sender, RoutedEventArgs e)
    {
        if (Workspace is not { Items.Count: > 0 } workspace || workspace.BlockedByLock())
        {
            return;
        }

        var gone = workspace.Items.Select(i => i.FullPath).ToList();
        workspace.Items.Clear();
        ActionLog.Info($"Список копирования очищен: убрано {gone.Count}", null, AppSection.Files, workspace, gone);
    }

    private void OnToggleAllItems(object sender, RoutedEventArgs e) => Workspace?.ToggleAllItems();

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

        // бросили на окно — файлы в него; мимо окон — в то, что в фокусе
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { DataContext: CopyWindow window })
            {
                workspace.FocusWindow(window);
                break;
            }
        }

        workspace.AddPaths(paths);
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

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        if (Workspace is not { } workspace || workspace.BlockedByLock())
        {
            return;
        }

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

        var dialog = new RemoteBrowserWindow(browseServer, SshService.NormalizeRemote(workspace.RemotePath))
        {
            Owner = Window.GetWindow(this),
        };

        if (dialog.ShowDialog() == true && dialog.SelectedPath is { } picked)
        {
            workspace.RemotePath = picked;
            settings.BrowseServerId = dialog.Server.Id;
            AppState.Current.SaveSettings();
        }
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

        // широкое действие: копирование уходит со всех вкладок сразу — сначала спросить.
        // По умолчанию фокус на «Отмене»
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
