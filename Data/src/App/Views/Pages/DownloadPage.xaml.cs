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
/// Страница скачивания — зеркало копирования: таблица файлов и папок, каждая строка со своего
/// сервера, папка здесь, те же правила «что качать» и исключения. У каждой вкладки свои строки и папка.
/// </summary>
public partial class DownloadPage : Page, Controls.IToastHost
{
    public DownloadPage()
    {
        InitializeComponent();
        BarToast.Follow(EverywhereSwitch);

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

    /// <summary>
    /// Обзор сервера с выбором нескольких файлов и папок разом. Сервер меняется прямо в обзоре —
    /// строки встают с тем сервером, на котором их выбрали.
    /// </summary>
    private void OnBrowseAdd(object sender, RoutedEventArgs e)
    {
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

    /// <summary>Двойной щелчок по строке — обзор её сервера: папка открывается сама, у файла — его папка.</summary>
    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Workspace is not { } workspace
            || e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(PathsList, source) is not DataGridRow { Item: DownloadItem item })
        {
            return;
        }

        if (item.Server is not { } server)
        {
            AppLog.Hint($"Сервер строки «{item.Name}» удалён из списка — открыть обзор не на чем.", LogLevel.Warning);
            return;
        }

        Browse(workspace, server, item.IsDirectory ? item.Path : SshService.ParentRemote(item.Path));
    }

    /// <summary>Обзор с выбором нескольких файлов и папок; выбранное встаёт строками с сервером обзора.</summary>
    private void Browse(Workspace workspace, ServerEntry server, string start)
    {
        var dialog = new RemoteBrowserWindow(server, start, pickMany: true)
        {
            Owner = Window.GetWindow(this),
        };

        if (dialog.ShowDialog() != true || dialog.SelectedItems.Count == 0)
        {
            return;
        }

        var added = workspace.AddDownloads(
            dialog.Server,
            dialog.SelectedItems.Select(i => (i.FullPath, i.IsDirectory, i.Length, i.Modified)));

        if (added > 0)
        {
            ActionLog.Info($"Добавлено для скачивания: {added} · всего {workspace.Downloads.Count}", dialog.Server, AppSection.Download, workspace,
                dialog.SelectedItems.Select(i => (i.IsDirectory ? "папка " : "файл ") + i.FullPath));
        }
        else
        {
            AppLog.Hint("Всё выбранное уже есть в списке.");
        }

        // у новых папок сразу видны число файлов и размер
        Check();
    }

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

        ActionLog.Info($"Путь строки сменён: {item.Name} → {dialog.Value.TrimEnd('/').Split('/').Last()}", item.Server, AppSection.Download, workspace,
            [$"было: {item.Path}", $"стало: {dialog.Value}"]);
    }

    /// <summary>Корзина в конце строки: строка уходит из списка, на сервере ничего не удаляется.</summary>
    private void OnRemoveRow(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DownloadItem item } && Workspace is { } workspace)
        {
            workspace.Downloads.Remove(item);
            ActionLog.Info($"Убрана строка: {item.Name}", item.Server, AppSection.Download, workspace,
                [item.Path, $"в списке осталось: {workspace.Downloads.Count}"]);
        }
    }

    private void OnRemoveSelected(object sender, RoutedEventArgs e)
    {
        if (Workspace is not { } workspace)
        {
            return;
        }

        var picked = PathsList.SelectedItems.OfType<DownloadItem>().ToList();

        if (picked.Count == 0)
        {
            AppLog.Hint("Выделите строки, которые убрать (Ctrl, Shift).");
            return;
        }

        foreach (var item in picked)
        {
            workspace.Downloads.Remove(item);
        }

        ActionLog.Info($"Убрано строк: {picked.Count}", null, AppSection.Download, workspace,
            picked.Select(i => $"{i.ServerLabel}: {i.Path}").Append($"в списке осталось: {workspace.Downloads.Count}"));
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (Workspace is { Downloads.Count: > 0 } workspace)
        {
            var gone = workspace.Downloads.Select(i => $"{i.ServerLabel}: {i.Path}").ToList();
            workspace.Downloads.Clear();
            ActionLog.Info($"Список скачивания очищен: убрано {gone.Count}", null, AppSection.Download, workspace, gone);
        }
    }

    private void OnPickFolder(object sender, RoutedEventArgs e)
    {
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

    Controls.ActionToast Controls.IToastHost.PageToast => BarToast;
}
