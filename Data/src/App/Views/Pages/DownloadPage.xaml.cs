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
/// Страница скачивания — зеркало копирования: вкладка делится на окна, таблица одна на все окна, строки окна —
/// своей группой, каждая строка со своего сервера, папка у окна своя, те же правила «что качать» и исключения.
/// Панель кнопок и параметры показывают окно в фокусе, фокус переходит щелчком по номеру окна.
/// </summary>
public partial class DownloadPage : Page
{
    /// <summary>Строки всех окон вкладки одним списком для общей таблицы.</summary>
    private readonly Controls.WindowRows _rows = new(downloads: true);

    public DownloadPage()
    {
        InitializeComponent();

        Controls.CopyWindowLayout.HookGroups(RowsGrid, _rows, ToggleWindowItems, FocusOn, AddWindowAfter, RemoveWindow, Rename);
        Controls.CopyWindowLayout.WatchFailures(this, () => Workspace);
        Controls.CopyWindowLayout.RestoreBand(BandRow, BandLeft);

        DataContextChanged += (_, _) => RowsGrid.ItemsSource = _rows.Attach(Workspace);
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

    /// <summary>Окно элемента: строки папки, разделителя, строки таблицы или заглушки пустого окна.</summary>
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

        if (Workspace is { } workspace)
        {
            workspace.SelectAllDownloads(workspace.AllDownloadsSelected != true);
        }
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
        if (Workspace is { } workspace && !workspace.BlockedByLock())
        {
            Controls.CopyWindowLayout.Show(RowsGrid, _rows, workspace.AddCopyWindow(after));
        }
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
            Controls.CopyWindowLayout.Arrange(grid, "downloads");
        }
    }

    /// <summary>
    /// Обзор сервера с выбором нескольких файлов и папок разом. Сервер меняется прямо в обзоре —
    /// строки встают с тем сервером, на котором их выбрали.
    /// </summary>
    private void OnBrowseAdd(object sender, RoutedEventArgs e) => BrowseInto(null);

    /// <param name="into">Окно, куда встанут строки; null — окно в фокусе.</param>
    private void BrowseInto(CopyWindow? into)
    {
        if (Workspace is not { } workspace)
        {
            return;
        }

        var settings = AppState.Current.Settings;
        var downloads = (into ?? workspace.FocusedWindow).Downloads;

        // обзор открывается на отмеченном слева сервере, из нескольких — на первом в списке;
        // ничего не отмечено — на сервере последней строки, как в прошлый раз
        var server =
            workspace.CopyTargets.FirstOrDefault()
            ?? downloads.LastOrDefault(d => d.Server is not null)?.Server
            ?? AppState.Current.Servers.FirstOrDefault(s => s.Id == settings.BrowseServerId)
            ?? AppState.Current.Servers.FirstOrDefault();

        if (server is null)
        {
            AppLog.Hint("Сначала добавьте хотя бы один сервер.", LogLevel.Warning);
            return;
        }

        // папка — последней строки этого же сервера, иначе последняя открытая
        var last = downloads.LastOrDefault(d => d.Server == server);
        var start = last is not null
            ? SshService.ParentRemote(last.Path)
            : settings.LastRemotePath;

        Browse(workspace, server, start, into);
    }

    /// <summary>Значок в поле «Куда скачать»: папка окна — в буфер обмена.</summary>
    private void OnCopyLocalFolder(object sender, RoutedEventArgs e)
    {
        var path = WindowOf(sender)?.LocalFolder?.Trim();

        if (string.IsNullOrEmpty(path))
        {
            AppLog.Hint("Папка для скачивания не указана — копировать нечего.", LogLevel.Warning);
            return;
        }

        if (ClipboardText.Copy(path))
        {
            AppLog.Echo("Путь скопирован: " + path);
        }
    }

    /// <summary>
    /// Двойной щелчок: мимо строк — как «Выбрать на сервере» для окна в фокусе, по пустому окну — для этого окна,
    /// по строке окна над его строками — «Обзор» папки этого окна, по строке — другой адрес для неё.
    /// </summary>
    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e) =>
        Controls.CopyWindowLayout.DoubleClick(e, () => BrowseInto(null), row =>
        {
            if (row is DownloadItem item)
            {
                PickForRow(item);
            }
            else if (row is System.Windows.Data.CollectionViewGroup && WindowOf(row) is { } picked)
            {
                // строка окна над его строками — «Обзор» папки этого окна: первый щелчок уже дал ему фокус
                OnPickFolder(picked, e);
            }
            else if (WindowOf(row) is { } window)
            {
                BrowseInto(window);
            }
        });

    /// <summary>Тот же обзор, с папки этой строки и на её сервере: первое выбранное встаёт на её место, остальное — новыми строками.</summary>
    private void PickForRow(DownloadItem item)
    {
        if (Workspace is not { } workspace)
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

        // тот же адрес — ничего не меняется
        if ((dialog.Server.Id != item.ServerId || path != item.Path)
            && workspace.ReplaceDownload(item, dialog.Server.Id, path, first.IsDirectory, first.Length, first.Modified) is { } problem)
        {
            AppLog.Hint("Путь не сменён: " + problem + ".", LogLevel.Warning);
        }

        if (dialog.SelectedItems.Count > 1)
        {
            AddPicked(workspace, dialog.Server, dialog.SelectedItems.Skip(1).ToList(), WindowOf(item));
        }

        Check();
    }

    /// <summary>Под замком обзор только показывает: «Выбрать» отказывает, и сюда ничего не приходит.</summary>
    private RemoteBrowserWindow? PickOnServer(ServerEntry server, string start)
    {
        var dialog = new RemoteBrowserWindow(server, start, pickMany: true, viewOnly: Workspace?.IsPaneLocked == true)
        {
            Owner = Window.GetWindow(this),
        };

        return dialog.ShowDialog() == true && dialog.SelectedItems.Count > 0 ? dialog : null;
    }

    /// <summary>Обзор с выбором нескольких файлов и папок; выбранное встаёт строками с сервером обзора.</summary>
    private void Browse(Workspace workspace, ServerEntry server, string start, CopyWindow? into)
    {
        if (PickOnServer(server, start) is not { } dialog)
        {
            return;
        }

        AddPicked(workspace, dialog.Server, dialog.SelectedItems, into);

        // у новых папок сразу видны число файлов и размер
        Check();
    }

    private static void AddPicked(Workspace workspace, ServerEntry server, IReadOnlyList<FsEntry> picked, CopyWindow? into = null)
    {
        var window = into ?? workspace.FocusedWindow;

        // правка списка в журнал не идёт
        if (workspace.AddDownloads(server, picked.Select(i => (i.FullPath, i.IsDirectory, i.Length, i.Modified)), window) == 0)
        {
            AppLog.Hint("Всё выбранное уже есть в списке.");
        }
    }

    private void OnToggleAll(object sender, RoutedEventArgs e)
    {
        if (Workspace is { } workspace)
        {
            // галочка в общей шапке трёхпозиционная и про все окна — после «частично» щелчок отмечает все
            workspace.SelectAllWindowsDownloads(workspace.AllWindowsDownloadsSelected != true);
        }
    }

    /// <summary>Под замком исключения открываются только посмотреть.</summary>
    private void OnExcludes(object sender, RoutedEventArgs e)
    {
        if (Workspace is { } workspace)
        {
            ExcludeDialog.Edit(Window.GetWindow(this), workspace, "скачивании", viewOnly: workspace.IsPaneLocked);
        }
    }

    /// <summary>Значок в конце строки: путь на сервере — в буфер.</summary>
    private void OnCopyRowPath(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DownloadItem item } && ClipboardText.Copy(item.Path))
        {
            AppLog.Echo("Путь скопирован: " + item.Path);
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
        }
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
            (WindowOf(item) ?? workspace.FocusedWindow).Downloads.Remove(item);
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
            // отмеченные, а без отметок — все строки окна
            var picked = workspace.Downloads.Where(i => i.IsSelected).ToList();
            var gone = picked.Count > 0 ? picked : workspace.Downloads.ToList();

            foreach (var item in gone)
            {
                workspace.Downloads.Remove(item);
            }

            ActionLog.Info(workspace.Downloads.Count == 0
                ? $"Список скачивания очищен: убрано {gone.Count}"
                : $"Из списка скачивания убрано: {gone.Count} · осталось {workspace.Downloads.Count}",
                null, AppSection.Download, workspace, gone.Select(i => $"{i.ServerLabel}: {i.Path}").ToList());
        }
    }

    /// <summary>«Обзор» в строке папки окна. Под замком папки можно посмотреть, но выбранная не встаёт: отказ плашкой.</summary>
    private void OnPickFolder(object sender, RoutedEventArgs e)
    {
        if (Workspace is not { } workspace)
        {
            return;
        }

        var window = WindowOf(sender) ?? workspace.FocusedWindow;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Куда скачать",
        };

        if (Directory.Exists(window.LocalFolder))
        {
            dialog.InitialDirectory = window.LocalFolder;
        }

        if (dialog.ShowDialog(Window.GetWindow(this)) == true && !workspace.BlockedByLock())
        {
            window.LocalFolder = dialog.FolderName;
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var folder = (WindowOf(sender) ?? Workspace?.FocusedWindow)?.LocalFolder.Trim() ?? string.Empty;

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
