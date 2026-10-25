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
public partial class DownloadPage : Page
{
    public DownloadPage()
    {
        InitializeComponent();

        DataContext = AppState.Current.ActiveWorkspace;
        AppState.Current.ActiveWorkspaceChanged += workspace => DataContext = workspace;

        // ярлыки вкладок пишут статистику по открытому разделу
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                AppState.Current.Section = AppSection.Download;
            }
        };
    }

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
        var last = workspace.Downloads.LastOrDefault(d => d.Server is not null);

        // открываем там, где выбирали в прошлый раз
        var server =
            last?.Server
            ?? workspace.CopyTargets.FirstOrDefault()
            ?? AppState.Current.Servers.FirstOrDefault(s => s.Id == settings.BrowseServerId)
            ?? AppState.Current.Servers.FirstOrDefault();

        if (server is null)
        {
            AppLog.Warn("Сначала добавьте хотя бы один сервер.");
            return;
        }

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
            AppLog.Warn($"Сервер строки «{item.Name}» удалён из списка — открыть обзор не на чем.", workspace.Title);
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

        AppLog.Info(
            added > 0
                ? $"Добавлено для скачивания с «{dialog.Server.PaneLabel}»: {added}."
                : "Всё выбранное уже есть в списке.",
            workspace.Title);
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
        if (Workspace is not { } workspace
            || ExcludeDialog.Ask(Window.GetWindow(this), workspace.ExcludeMasks) is not { } masks
            || masks == workspace.ExcludeMasks)
        {
            return;
        }

        workspace.ExcludeMasks = masks;
        var count = ExcludeRules.Count(masks);
        AppLog.Info(count > 0 ? $"Исключения при скачивании: {count}." : "Исключения при скачивании сняты.", workspace.Title);
    }

    private void OnRemoveSelected(object sender, RoutedEventArgs e)
    {
        if (Workspace is not { } workspace)
        {
            return;
        }

        var picked = PathsList.SelectedItems.OfType<DownloadItem>().ToList();

        foreach (var item in picked)
        {
            workspace.Downloads.Remove(item);
        }

        AppLog.Info(
            picked.Count > 0 ? $"Убрано строк: {picked.Count}." : "Выделите строки, которые убрать (Ctrl, Shift).",
            workspace.Title);
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (Workspace is { Downloads.Count: > 0 } workspace)
        {
            workspace.Downloads.Clear();
            AppLog.Info("Список путей для скачивания очищен.", workspace.Title);
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
            AppLog.Warn(folder.Length == 0 ? "Папка не указана." : $"Папки ещё нет: {folder}. Она появится при первом скачивании.");
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    /// <summary>«Выполнить тут» — только эта вкладка: её пути, её папка, её отмеченные серверы.</summary>
    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        if (Workspace is { } workspace)
        {
            await workspace.RunDownloadAsync();
        }
    }

    /// <summary>
    /// «Выполнить везде» — скачивание во всех вкладках сразу, каждая со своими условиями.
    /// Сводка показывается на той вкладке, с которой нажали.
    /// </summary>
    private async void OnDownloadEverywhere(object sender, RoutedEventArgs e)
    {
        if (Workspace is not { } workspace)
        {
            return;
        }

        var all = AppState.Current.DownloadTabs.ToList();

        if (!ConfirmDialog.Ask(
                Window.GetWindow(this),
                $"Запустить скачивание на всех вкладках ({all.Count})?",
                string.Empty,
                "Скачать везде"))
        {
            AppLog.Info("«Выполнить везде» отменено.");
            return;
        }

        if (all.Count <= 1)
        {
            await workspace.RunDownloadAsync();
            return;
        }

        AppLog.Info($"«Выполнить везде» — скачивание запускается во всех вкладках: {all.Count}.");

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
