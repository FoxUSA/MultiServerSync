using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using MultiServerSync.Models;
using MultiServerSync.Services;
using MultiServerSync.Views.Dialogs;

namespace MultiServerSync.Views.Pages;

/// <summary>
/// Страница копирования. Список файлов, путь и параметры принадлежат текущей вкладке,
/// история путей на сервере общая для всех вкладок.
/// </summary>
public partial class FilesPage : Page
{
    private bool _activationHooked;

    public FilesPage()
    {
        InitializeComponent();

        // история путей сквозная — одна на все вкладки
        RemotePathBox.ItemsSource = AppState.Current.Settings.RemotePathHistory;

        DataContext = AppState.Current.ActiveWorkspace;
        AppState.Current.ActiveWorkspaceChanged += workspace => DataContext = workspace;

        // порядок в таблице выбирается щелчком по заголовку и не сбрасывается
        GridSort.Keep(ItemsGrid, "files");

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

    /// <summary>
    /// Одно окно на всё: в нём выбираются и файлы, и папки, поодиночке или пачкой.
    /// Разделять их заранее двумя кнопками незачем — на сервер всё уходит одинаково.
    /// </summary>
    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this) is { } window
            ? new WindowInteropHelper(window).Handle
            : IntPtr.Zero;

        var workspace = Workspace;
        var picked = LocalPicker.Pick(owner, workspace?.LastLocalFolder);

        if (picked.Count > 0 && workspace is not null)
        {
            // в следующий раз окно этой вкладки откроется там же, откуда добавляли сейчас
            workspace.LastLocalFolder = System.IO.Path.GetDirectoryName(picked[0].TrimEnd(System.IO.Path.DirectorySeparatorChar)) ?? string.Empty;
            workspace.AddPaths(picked);
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
        var count = Core.ExcludeRules.Count(masks);
        AppLog.Info(count > 0 ? $"Исключения при копировании: {count}." : "Исключения при копировании сняты.", workspace.Title);
    }

    private void OnRemoveSelected(object sender, RoutedEventArgs e) =>
        Workspace?.RemoveItems(ItemsGrid.SelectedItems.OfType<TransferItem>());

    private void OnClearItems(object sender, RoutedEventArgs e) => Workspace?.Items.Clear();

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
        var typed = RemotePathBox.Text;

        if (!settings.RemotePathHistory.Remove(value))
        {
            return;
        }

        AppState.Current.SaveSettings();
        AppLog.Info($"Из истории путей удалено: {value}");

        // удаление выбранной строки очищает поле ввода — возвращаем то, что там было
        Dispatcher.BeginInvoke(new Action(() => RemotePathBox.Text = typed));

        RemotePathBox.IsDropDownOpen = settings.RemotePathHistory.Count > 0;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            Workspace?.AddPaths(paths);
        }
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
        if (Workspace is not { } workspace)
        {
            return;
        }

        var settings = AppState.Current.Settings;

        var browseServer =
            AppState.Current.Servers.FirstOrDefault(s => s.Id == settings.BrowseServerId)
            ?? workspace.CopyTargets.FirstOrDefault()
            ?? AppState.Current.Servers.FirstOrDefault();

        if (browseServer is null)
        {
            AppLog.Warn("Сначала добавьте хотя бы один сервер.");
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

    /// <summary>«Выполнить тут» — только эта вкладка: её файлы, её путь, её отмеченные серверы.</summary>
    private async void OnCopy(object sender, RoutedEventArgs e)
    {
        if (Workspace is { } workspace)
        {
            await workspace.RunCopyAsync();
        }
    }

    /// <summary>
    /// «Выполнить везде» — копирование запускается во всех вкладках сразу, каждая со своими
    /// условиями. Кнопка есть на любой вкладке; сводка показывается на той, с которой нажали.
    /// </summary>
    private async void OnCopyEverywhere(object sender, RoutedEventArgs e)
    {
        if (Workspace is not { } workspace)
        {
            return;
        }

        var all = AppState.Current.Workspaces.ToList();

        // широкое действие: копирование уходит со всех вкладок сразу — сначала спросить.
        // По умолчанию фокус на «Отмене»
        if (!ConfirmDialog.Ask(
                Window.GetWindow(this),
                $"Запустить копирование на всех вкладках ({all.Count})?",
                string.Empty,
                "Копировать везде"))
        {
            AppLog.Info("«Выполнить везде» отменено.");
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
            AppLog.Info("«Выполнить везде»: запускать нечего — все вкладки пропущены.");
            return;
        }

        if (chosen.Count == 1)
        {
            await chosen[0].RunCopyAsync(approved: true);
            return;
        }

        AppLog.Info($"«Выполнить везде» — копирование запускается во всех вкладках: {chosen.Count}.");

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
