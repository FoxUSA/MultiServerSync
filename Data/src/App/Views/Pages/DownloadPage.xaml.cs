using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MultiServerSync.Core;
using MultiServerSync.Models;
using MultiServerSync.Services;
using MultiServerSync.Views.Dialogs;

namespace MultiServerSync.Views.Pages;

/// <summary>
/// Страница скачивания — зеркало копирования: вкладка делится на окна, в каждом таблица файлов и папок,
/// каждая строка со своего сервера, папка здесь, те же правила «что качать» и исключения.
/// Панель кнопок и параметры показывают окно в фокусе.
/// </summary>
public partial class DownloadPage : Page
{
    public DownloadPage()
    {
        InitializeComponent();

        Controls.CopyWindowLayout.Follow(WindowsScroll, WindowsList);
        Controls.CopyWindowLayout.Reorder(WindowsScroll, WindowsList, () => Workspace);
        Controls.CopyWindowLayout.WatchFailures(this, () => Workspace);
        Controls.CopyWindowLayout.RestoreBand(BandRow, BandLeft);

        DataContext = AppState.Current.ActiveWorkspace;
        AppState.Current.ActiveWorkspaceChanged += workspace =>
        {
            DataContext = workspace;
            Check();
        };

        // с сервера изменений не пришлют — пока страница на виду, список перечитывается сам
        _checkTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _checkTimer.Tick += (_, _) => Check();

        // ярлыки вкладок пишут статистику по открытому разделу
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                AppState.Current.Section = AppSection.Download;
                Check();
                _checkTimer.Start();
            }
            else
            {
                _checkTimer.Stop();
            }
        };
    }

    private readonly System.Windows.Threading.DispatcherTimer _checkTimer;

    private void OnBandMoved(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) =>
        Controls.CopyWindowLayout.SaveBand(BandRow, BandLeft);

    /// <summary>Размер, файлы и даты в списке — заново с серверов, если открыта вкладка скачивания.</summary>
    private void Check()
    {
        if (IsVisible && Workspace is { } workspace && AppState.Current.DownloadTabs.Contains(workspace))
        {
            _ = workspace.RefreshDownloadsAsync();
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Check();

    private Workspace? Workspace => DataContext as Workspace;

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
        if (sender is FrameworkElement { DataContext: CopyWindow after } && Workspace is { } workspace && !workspace.BlockedByLock())
        {
            Controls.CopyWindowLayout.Show(WindowsList, workspace.AddCopyWindow(after));
        }
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
            Controls.CopyWindowLayout.Arrange(grid, "downloads");
        }
    }

    /// <summary>
    /// Обзор сервера с выбором нескольких файлов и папок разом. Сервер меняется прямо в обзоре —
    /// строки встают с тем сервером, на котором их выбрали.
    /// </summary>
    private void OnBrowseAdd(object sender, RoutedEventArgs e)
    {
        if (Workspace?.BlockedByLock() == true)
        {
            return;
        }

        if (Workspace is not { } workspace)
        {
            return;
        }

        var settings = AppState.Current.Settings;

        // обзор открывается на отмеченном слева сервере, из нескольких — на первом в списке;
        // ничего не отмечено — на сервере последней строки, как в прошлый раз
        var server =
            workspace.CopyTargets.FirstOrDefault()
            ?? workspace.Downloads.LastOrDefault(d => d.Server is not null)?.Server
            ?? AppState.Current.Servers.FirstOrDefault(s => s.Id == settings.BrowseServerId)
            ?? AppState.Current.Servers.FirstOrDefault();

        if (server is null)
        {
            AppLog.Hint("Сначала добавьте хотя бы один сервер.", LogLevel.Warning);
            return;
        }

        // папка — последней строки этого же сервера, иначе последняя открытая
        var last = workspace.Downloads.LastOrDefault(d => d.Server == server);
        var start = last is not null
            ? SshService.ParentRemote(last.Path)
            : settings.LastRemotePath;

        Browse(workspace, server, start);
    }

    /// <summary>Значок в поле «Куда скачать»: папка вкладки — в буфер обмена.</summary>
    private void OnCopyLocalFolder(object sender, RoutedEventArgs e)
    {
        var path = Workspace?.LocalFolder?.Trim();

        if (string.IsNullOrEmpty(path))
        {
            AppLog.Hint("Папка для скачивания не указана — копировать нечего.", LogLevel.Warning);
            return;
        }

        if (ClipboardText.Copy(path))
        {
            AppLog.Hint("Путь скопирован: " + path);
        }
    }

    /// <summary>Двойной щелчок: мимо строк — как «Выбрать на сервере» для этого окна, по строке — другой адрес для неё.</summary>
    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e) =>
        Controls.CopyWindowLayout.DoubleClick(e, () => OnBrowseAdd(sender, e), row => PickForRow((DownloadItem)row));

    /// <summary>Тот же обзор, с папки этой строки и на её сервере: первое выбранное встаёт на её место, остальное — новыми строками.</summary>
    private void PickForRow(DownloadItem item)
    {
        if (Workspace is not { } workspace || workspace.BlockedByLock())
        {
            return;
        }

        if (item.Server is not { } server)
        {
            AppLog.Hint($"Сервер строки «{item.Name}» удалён из списка — открыть обзор не на чем.", LogLevel.Warning);
            return;
        }

        if (PickOnServer(server, SshService.ParentRemote(item.Path)) is not { } dialog)
        {
            return;
        }

        var first = dialog.SelectedItems[0];
        var path = SshService.NormalizeRemote(first.FullPath);

        // тот же адрес — ничего не меняется и в журнал не идёт
        if (dialog.Server.Id != item.ServerId || path != item.Path)
        {
            if (workspace.ReplaceDownload(item, dialog.Server.Id, path, first.IsDirectory, first.Length, first.Modified) is { } problem)
            {
                AppLog.Hint("Путь не сменён: " + problem + ".", LogLevel.Warning);
            }
            else
            {
                LogReplaced(workspace, item, dialog.Server, path);
            }
        }

        if (dialog.SelectedItems.Count > 1)
        {
            AddPicked(workspace, dialog.Server, dialog.SelectedItems.Skip(1).ToList());
        }

        Check();
    }

    private RemoteBrowserWindow? PickOnServer(ServerEntry server, string start)
    {
        var dialog = new RemoteBrowserWindow(server, start, pickMany: true)
        {
            Owner = Window.GetWindow(this),
        };

        return dialog.ShowDialog() == true && dialog.SelectedItems.Count > 0 ? dialog : null;
    }

    /// <summary>Обзор с выбором нескольких файлов и папок; выбранное встаёт строками с сервером обзора.</summary>
    private void Browse(Workspace workspace, ServerEntry server, string start)
    {
        if (PickOnServer(server, start) is not { } dialog)
        {
            return;
        }

        AddPicked(workspace, dialog.Server, dialog.SelectedItems);

        // у новых папок сразу видны число файлов и размер
        Check();
    }

    private static void AddPicked(Workspace workspace, ServerEntry server, IReadOnlyList<FsEntry> picked)
    {
        var added = workspace.AddDownloads(server, picked.Select(i => (i.FullPath, i.IsDirectory, i.Length, i.Modified)));

        if (added > 0)
        {
            ActionLog.Info($"Добавлено для скачивания: {added} · всего {workspace.Downloads.Count}", server, AppSection.Download, workspace,
                picked.Select(i => (i.IsDirectory ? "папка " : "файл ") + i.FullPath));
        }
        else
        {
            AppLog.Hint("Всё выбранное уже есть в списке.");
        }
    }

    private static void LogReplaced(Workspace workspace, DownloadItem item, ServerEntry? server, string path) =>
        ActionLog.Info($"Путь строки сменён: {item.Name} → {path.TrimEnd('/').Split('/').Last()}", server, AppSection.Download, workspace,
            [$"было: {item.Path}", $"стало: {path}"]);

    private void OnToggleAll(object sender, RoutedEventArgs e)
    {
        if (Workspace is { } workspace)
        {
            // галочка в шапке трёхпозиционная — после «частично» щелчок отмечает все
            workspace.SelectAllDownloads(workspace.AllDownloadsSelected != true);
        }
    }

    private void OnExcludes(object sender, RoutedEventArgs e)
    {
        if (Workspace?.BlockedByLock() == true)
        {
            return;
        }

        if (Workspace is { } workspace)
        {
            ExcludeDialog.Edit(Window.GetWindow(this), workspace, "скачивании");
        }
    }

    /// <summary>Значок в конце строки: путь на сервере — в буфер.</summary>
    private void OnCopyRowPath(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DownloadItem item } && ClipboardText.Copy(item.Path))
        {
            AppLog.Hint("Путь скопирован: " + item.Path);
        }
    }

    /// <summary>Значок в конце строки: вписать другой путь на том же сервере — строка встаёт на то же место.</summary>
    private void OnEditRowPath(object sender, RoutedEventArgs e)
    {
        if (Workspace?.BlockedByLock() == true)
        {
            return;
        }

        if (sender is not FrameworkElement { DataContext: DownloadItem item } || Workspace is not { } workspace)
        {
            return;
        }

        var dialog = new InputDialog("Путь строки", $"Путь на сервере «{item.ServerLabel}»:", item.Path) { Owner = Window.GetWindow(this) };

        if (dialog.ShowDialog() != true || dialog.Value.Length == 0 || dialog.Value == item.Path)
        {
            return;
        }

        if (workspace.ReplaceDownloadPath(item, dialog.Value) is { } problem)
        {
            AppLog.Hint("Путь не сменён: " + problem + ".", LogLevel.Warning);
            return;
        }

        LogReplaced(workspace, item, item.Server, dialog.Value);
    }

    /// <summary>Корзина в конце строки: строка уходит из списка, на сервере ничего не удаляется.</summary>
    private void OnRemoveRow(object sender, RoutedEventArgs e)
    {
        if (Workspace?.BlockedByLock() == true)
        {
            return;
        }

        if (sender is FrameworkElement { DataContext: DownloadItem item } && Workspace is { } workspace)
        {
            workspace.Downloads.Remove(item);
            ActionLog.Info($"Убрана строка: {item.Name}", item.Server, AppSection.Download, workspace,
                [item.Path, $"в списке осталось: {workspace.Downloads.Count}"]);
        }
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (Workspace?.BlockedByLock() == true)
        {
            return;
        }

        if (Workspace is { Downloads.Count: > 0 } workspace)
        {
            var gone = workspace.Downloads.Select(i => $"{i.ServerLabel}: {i.Path}").ToList();
            workspace.Downloads.Clear();
            ActionLog.Info($"Список скачивания очищен: убрано {gone.Count}", null, AppSection.Download, workspace, gone);
        }
    }

    private void OnPickFolder(object sender, RoutedEventArgs e)
    {
        if (Workspace?.BlockedByLock() == true)
        {
            return;
        }

        if (Workspace is not { } workspace)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Куда скачать",
        };

        if (Directory.Exists(workspace.LocalFolder))
        {
            dialog.InitialDirectory = workspace.LocalFolder;
        }

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            workspace.LocalFolder = dialog.FolderName;
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var folder = Workspace?.LocalFolder.Trim() ?? string.Empty;

        if (!Directory.Exists(folder))
        {
            AppLog.Hint(folder.Length == 0 ? "Папка не указана." : $"Папки ещё нет: {folder}. Она появится при первом скачивании.", LogLevel.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    /// <summary>«Выполнить»: эта вкладка — её пути, папка и серверы; со свитчем «Везде» — все вкладки.</summary>
    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        if (Workspace is not { } workspace)
        {
            return;
        }

        if (AppState.Current.Everywhere)
        {
            await DownloadEverywhereAsync(workspace);
        }
        else
        {
            await workspace.RunDownloadAsync();
        }
    }

    /// <summary>
    /// «Сравнить»: файлы сервера против папки здесь по правилам из параметров, без скачивания.
    /// Со свитчем «Везде» — на всех вкладках сразу и без вопроса: ничего не меняется.
    /// </summary>
    private async void OnCompare(object sender, RoutedEventArgs e)
    {
        if (Workspace is not { } workspace)
        {
            return;
        }

        var all = AppState.Current.Everywhere ? AppState.Current.DownloadTabs.ToList() : [workspace];

        if (all.Count <= 1)
        {
            await workspace.RunCompareDownloadAsync();
            return;
        }

        AppLog.Info($"Сравнение на всех вкладках: {all.Count}.");

        var running = all.Select(w => w.RunCompareDownloadAsync(notify: false)).ToList();
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
    /// «Выполнить» со свитчем «Везде» — скачивание во всех вкладках сразу, каждая со своими условиями.
    /// Сводка показывается на той вкладке, с которой нажали.
    /// </summary>
    private async Task DownloadEverywhereAsync(Workspace workspace)
    {
        var all = AppState.Current.DownloadTabs.ToList();

        if (!ConfirmDialog.Ask(
                Window.GetWindow(this),
                $"Запустить скачивание на всех вкладках ({all.Count})?",
                string.Empty,
                "Скачать везде"))
        {
            AppLog.Hint("Запуск на всех вкладках отменён.");
            return;
        }

        if (all.Count <= 1)
        {
            await workspace.RunDownloadAsync();
            return;
        }

        AppLog.Info($"Везде: скачивание запускается во всех вкладках: {all.Count}.");

        // операции стартуют синхронно, поэтому строки уже созданы и попадут в сводку
        var running = all.Select(w => w.RunDownloadAsync(notify: false)).ToList();
        var started = all.Where((_, i) => !(running[i].IsCompletedSuccessfully && !running[i].Result)).ToList();

        if (started.Count == 0)
        {
            AppLog.Warn("Скачивание не начато ни на одной вкладке — причины выше.");
            await Task.WhenAll(running);
            return;
        }

        workspace.ShowSummary(started.Select(w => w.CopyBatch).ToList(), "скачивание");

        await Task.WhenAll(running);

        AppState.Current.NotifyFinished(workspace.SummaryBatch);
    }

}
