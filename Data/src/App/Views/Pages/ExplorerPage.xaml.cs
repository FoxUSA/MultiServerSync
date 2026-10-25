using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MultiServerSync.Models;
using MultiServerSync.Services;
using MultiServerSync.Views.Controls;
using MultiServerSync.Views.Dialogs;

namespace MultiServerSync.Views.Pages;

/// <summary>
/// «Проводник»: слева этот компьютер, справа сервер вкладки (он отмечен в панели «Целевые
/// сервера»). Перенос между ними — та же заливка и то же скачивание, что у «Копирования»
/// и «Скачивания», с теми же параметрами; внутри одной стороны — копирование и перенос на месте.
/// </summary>
public partial class ExplorerPage : Page
{
    /// <summary>Этот компьютер — один на все вкладки: кэш папок общий.</summary>
    private static readonly LocalSource Local = new();

    /// <summary>Переносы одной вкладки идут по очереди: канал до сервера один.</summary>
    private static readonly Dictionary<Workspace, Task> Queues = new();

    private readonly DispatcherTimer _saveDelay;
    private Workspace? _tab;
    private RemoteSource? _remote;
    private FilePane _active;
    private Clipped? _clip;
    private int _attachVersion;

    private sealed record Clipped(FilePane Pane, IReadOnlyList<FsEntry> Items, bool Cut);

    public ExplorerPage()
    {
        InitializeComponent();

        _active = RemotePane;

        _saveDelay = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _saveDelay.Tick += (_, _) =>
        {
            _saveDelay.Stop();
            AppState.Current.SaveWorkspaces();
        };

        foreach (var pane in new[] { LocalPane, RemotePane })
        {
            pane.Activated += SetActive;
            pane.CommandRequested += OnPaneCommand;
            pane.DropRequested += OnPaneDrop;
            pane.StateChanged += OnPaneState;
        }

        LocalPane.List.SelectionChanged += (_, _) => UpdateTransferButtons();
        RemotePane.List.SelectionChanged += (_, _) => UpdateTransferButtons();

        PreviewKeyDown += OnPageKeyDown;

        AppState.Current.ActiveWorkspaceChanged += workspace =>
        {
            if (IsVisible && AppState.Current.ExplorerTabs.Contains(workspace))
            {
                _ = AttachAsync(workspace);
            }
        };

        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                return;
            }

            AppState.Current.Section = AppSection.Explorer;

            if (!ReferenceEquals(_tab, AppState.Current.ActiveWorkspace))
            {
                _ = AttachAsync(AppState.Current.ActiveWorkspace);
            }
        };
    }

    // ── вкладка и сервер ──────────────────────────────────────────────────────

    private async Task AttachAsync(Workspace tab)
    {
        var version = ++_attachVersion;

        if (_tab is not null)
        {
            _tab.PropertyChanged -= OnTabChanged;
        }

        _tab = tab;
        DataContext = tab;
        tab.PropertyChanged += OnTabChanged;

        SetActive(tab.Explorer.RemoteActive ? RemotePane : LocalPane);

        await LocalPane.AttachAsync(Local, tab.Explorer.Local, LocalSource.ComputerTitle, ShellIcons.Computer);

        if (version == _attachVersion)
        {
            await AttachRemoteAsync(tab);
        }

        if (version == _attachVersion)
        {
            _active.FocusList();
        }
    }

    /// <summary>Сервер сменили в панели слева — правая половина переключается на него.</summary>
    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Workspace.ExplorerServer) && sender is Workspace tab && ReferenceEquals(tab, _tab)
            && !ReferenceEquals(tab.ExplorerServer, _remote?.Server))
        {
            _ = AttachRemoteAsync(tab);
        }
    }

    private async Task AttachRemoteAsync(Workspace tab)
    {
        var server = tab.ExplorerServer;

        if (server is null)
        {
            ReleaseRemote();
            RemotePane.ShowEmpty("Сервер", ShellIcons.Server,
                "Отметьте сервер в панели «Целевые сервера» слева — здесь откроются его папки.");
            UpdateTransferButtons();
            return;
        }

        if (_remote is null || !ReferenceEquals(_remote.Server, server))
        {
            ReleaseRemote();
            _remote = new RemoteSource(RemoteSession.Acquire(server, AppState.Current.Settings));
        }

        // папка с другого сервера тут не годится — открываем домашнюю
        var state = tab.Explorer;

        if (state.RemoteServerId != server.Id)
        {
            state.RemoteServerId = server.Id;
            state.Remote.Path = string.Empty;
            state.Remote.Focus = string.Empty;
        }

        var title = string.IsNullOrWhiteSpace(server.User) ? server.PaneLabel : $"{server.PaneLabel} ({server.User})";
        await RemotePane.AttachAsync(_remote, state.Remote, title, ShellIcons.Server);
        UpdateTransferButtons();
    }

    private void ReleaseRemote()
    {
        _remote?.Session.Release();
        _remote = null;
    }

    private void OnPaneState(FilePane pane)
    {
        if (_tab is null)
        {
            return;
        }

        if (ReferenceEquals(pane, RemotePane))
        {
            _tab.RaiseExplorerPath();
        }

        _saveDelay.Stop();
        _saveDelay.Start();
    }

    private void SetActive(FilePane pane)
    {
        _active = pane;
        LocalPane.SetActive(ReferenceEquals(pane, LocalPane));
        RemotePane.SetActive(ReferenceEquals(pane, RemotePane));

        if (_tab is not null && _tab.Explorer.RemoteActive != ReferenceEquals(pane, RemotePane))
        {
            _tab.Explorer.RemoteActive = ReferenceEquals(pane, RemotePane);
            _saveDelay.Stop();
            _saveDelay.Start();
        }
    }

    private FilePane Other(FilePane pane) => ReferenceEquals(pane, LocalPane) ? RemotePane : LocalPane;

    private void OnPageKeyDown(object sender, KeyEventArgs e)
    {
        // Tab — в соседнюю панель, как в двухпанельных программах; в полях ввода Tab свой
        if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None && e.OriginalSource is not TextBox)
        {
            e.Handled = true;
            var other = Other(_active);
            SetActive(other);
            other.FocusList();
        }
    }

    private void UpdateTransferButtons()
    {
        UploadButton.IsEnabled = _remote is not null && LocalPane.SelectedEntries.Count > 0;
        DownloadButton.IsEnabled = _remote is not null && RemotePane.SelectedEntries.Count > 0
            && LocalPane.CurrentPath.Length > 0;
    }

    // ── перенос ───────────────────────────────────────────────────────────────

    private void OnUpload(object sender, RoutedEventArgs e) => _ = TransferAsync(LocalPane, LocalPane.SelectedEntries, RemotePane.CurrentPath);

    private void OnDownload(object sender, RoutedEventArgs e) => _ = TransferAsync(RemotePane, RemotePane.SelectedEntries, LocalPane.CurrentPath);

    /// <summary>
    /// Между компьютером и сервером — общей заливкой и общим скачиванием с параметрами вкладки:
    /// права и владелец на сервере ставятся так же, как при «Копировании». Внутри одной
    /// стороны — копирование на месте.
    /// </summary>
    private Task TransferAsync(FilePane from, IReadOnlyList<FsEntry> items, string targetDir, bool move = false)
    {
        if (_tab is not { } tab || items.Count == 0)
        {
            return Task.CompletedTask;
        }

        var to = ReferenceEquals(from, LocalPane) && from.Source is LocalSource ? RemotePane : LocalPane;

        if (from.Source is RemoteSource && targetDir.Length == 0)
        {
            AppLog.Warn("Слева открыт «Этот компьютер» — откройте папку, куда скачивать.", tab.Title);
            AppState.Current.RequestLog();
            return Task.CompletedTask;
        }

        if (_remote is not { } remote)
        {
            AppLog.Warn("Сервер не выбран — отметьте его в панели «Целевые сервера» слева.", tab.Title);
            AppState.Current.RequestLog();
            return Task.CompletedTask;
        }

        if (move)
        {
            AppLog.Info("Между компьютером и сервером только копирование: исходные файлы остаются на месте.", tab.Title);
        }

        if (tab.CopyBatch.IsRunning)
        {
            AppLog.Info("Копирование встало в очередь — начнётся, когда закончится текущее.", tab.Title);
        }

        var server = remote.Server;
        var options = tab.CurrentOptions;
        var verify = tab.VerifyChecksums;
        var names = items.Select(i => i.Name).ToList();

        return Enqueue(tab, async () =>
        {
            if (from.Source is LocalSource)
            {
                AppLog.Info($"На {server.PaneLabel}: {Count(items)} в {targetDir}", tab.Title);
                var payload = items.Select(i => new TransferItem(i.FullPath)).ToList();
                await tab.UploadCoreAsync(payload, targetDir, [server], options, verify);
                remote.Invalidate(targetDir);
            }
            else
            {
                AppLog.Info($"С {server.PaneLabel}: {Count(items)} в {targetDir}", tab.Title);
                var plan = new Dictionary<ServerEntry, List<string>> { [server] = items.Select(i => i.FullPath).ToList() };
                await tab.DownloadCoreAsync(plan, targetDir, options, verify);
            }

            AppState.Current.NotifyFinished(tab.CopyBatch);
            await RefreshShowingAsync(to, targetDir, names);
        });
    }

    private static Task Enqueue(Workspace tab, Func<Task> work)
    {
        var previous = Queues.GetValueOrDefault(tab) ?? Task.CompletedTask;
        var next = After(previous, work);
        Queues[tab] = next;
        return next;

        static async Task After(Task previous, Func<Task> work)
        {
            try
            {
                await previous;
            }
            catch
            {
                // прошлая операция уже рассказала о своей ошибке
            }

            await work();
        }
    }

    /// <summary>Панель показывает папку, куда только что положили файлы, — перечитать и выделить их.</summary>
    private static async Task RefreshShowingAsync(FilePane pane, string dir, IReadOnlyCollection<string> names)
    {
        if (pane.Source is null || !string.Equals(pane.Source.Normalize(pane.CurrentPath), pane.Source.Normalize(dir), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await pane.RefreshAsync();
        pane.SelectNames(names);
    }

    private static string Count(IReadOnlyList<FsEntry> items)
    {
        var dirs = items.Count(i => i.IsDirectory);
        var files = items.Count - dirs;
        return string.Join(", ", new[] { dirs > 0 ? $"папок {dirs}" : null, files > 0 ? $"файлов {files}" : null }.Where(s => s is not null));
    }

    /// <summary>Внутри одной стороны: на сервере — cp -a / mv, на компьютере — средствами Windows.</summary>
    private async Task CopyWithinAsync(FilePane pane, IReadOnlyList<FsEntry> items, string targetDir, bool move)
    {
        if (pane.Source is not { } source || items.Count == 0)
        {
            return;
        }

        if (!source.IsRemote && targetDir.Length == 0)
        {
            AppLog.Warn("В «Этот компьютер» вставить нельзя — откройте папку.");
            return;
        }

        var replace = false;

        // на компьютере о совпадениях спросит сама Windows; на сервере — мы, одним вопросом на всё
        if (source.IsRemote)
        {
            var target = source.Normalize(targetDir);
            var there = string.Equals(source.Normalize(pane.CurrentPath), target, StringComparison.Ordinal)
                ? pane.Entries
                : await source.ListAsync(target, refresh: true);
            var taken = there.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
            var clashes = items.Where(i => taken.Contains(i.Name) && source.Parent(i.FullPath) != target).ToList();

            if (clashes.Count > 0)
            {
                var list = string.Join("\n", clashes.Take(10).Select(c => c.Name)) + (clashes.Count > 10 ? $"\nи ещё {clashes.Count - 10}" : string.Empty);
                var choice = ConfirmDialog.Choose(
                    Window.GetWindow(this),
                    $"В папке уже есть {clashes.Count} с такими же именами",
                    $"{list}\n\nПапки при замене сливаются: совпадающие файлы заменяются, лишние в назначении остаются.",
                    "Заменить",
                    "Пропустить их");

                if (choice == 0)
                {
                    return;
                }

                replace = choice == 1;
            }
        }

        var where = source is RemoteSource remote ? remote.Server.Display : LocalSource.ComputerTitle;

        try
        {
            var notes = await source.CopyHereAsync(items, targetDir, move, replace);

            foreach (var note in notes)
            {
                AppLog.Warn(note, where);
            }

            AppLog.Success($"{(move ? "Перенесено" : "Скопировано")}: {Count(items)} в {source.Display(targetDir)}", where);
        }
        catch (Exception ex)
        {
            AppLog.Error($"{(move ? "Перенос" : "Копирование")} не удалось: {ex.Message}", where);
        }

        if (move && pane.Source is { } s)
        {
            s.Invalidate(pane.CurrentPath);
            await pane.RefreshAsync();
        }

        source.Invalidate(targetDir);
        await RefreshShowingAsync(pane, targetDir, items.Select(i => i.Name).ToList());
        await RefreshShowingAsync(Other(pane), targetDir, items.Select(i => i.Name).ToList());
    }

    // ── буфер обмена и перетаскивание ─────────────────────────────────────────

    private void OnPaneDrop(FilePane pane, PaneDrop drop)
    {
        if (drop.From is { } from)
        {
            if (ReferenceEquals(from.Source, pane.Source) || (from.Source is RemoteSource a && pane.Source is RemoteSource b && a.Server == b.Server))
            {
                _ = CopyWithinAsync(pane, drop.Items, drop.TargetDir, drop.Move);
            }
            else
            {
                _ = TransferAsync(from, drop.Items, drop.TargetDir, drop.Move);
            }

            return;
        }

        _ = PasteWindowsFilesAsync(pane, drop.WindowsFiles, drop.TargetDir, drop.Move);
    }

    /// <summary>Файлы из проводника Windows (перетащили или Ctrl+C там): на сервер — заливкой, сюда — копией.</summary>
    private Task PasteWindowsFilesAsync(FilePane pane, IReadOnlyList<string> files, string targetDir, bool move)
    {
        var items = files
            .Where(f => File.Exists(f) || Directory.Exists(f))
            .Select(f => new FsEntry(Path.GetFileName(f.TrimEnd('\\')), f, Directory.Exists(f), 0, default))
            .ToList();

        if (items.Count == 0)
        {
            return Task.CompletedTask;
        }

        if (pane.Source is RemoteSource)
        {
            return TransferAsync(LocalPane, items, targetDir);
        }

        return CopyWithinAsync(pane, items, targetDir, move);
    }

    private async void OnPaneCommand(FilePane pane, PaneCommand command)
    {
        try
        {
            await RunCommandAsync(pane, command);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex.Message, _tab?.Title);
        }
    }

    private async Task RunCommandAsync(FilePane pane, PaneCommand command)
    {
        var picked = pane.SelectedEntries;
        var focused = pane.FocusedEntry;

        switch (command)
        {
            case PaneCommand.Copy:
            case PaneCommand.Cut:
                if (picked.Count == 0)
                {
                    return;
                }

                _clip = new Clipped(pane, picked, command == PaneCommand.Cut);

                // файлы компьютера — и в буфер Windows: их можно вставить в обычный проводник
                if (pane.Source is LocalSource)
                {
                    var list = new System.Collections.Specialized.StringCollection();
                    list.AddRange(picked.Select(p => p.FullPath).ToArray());

                    try
                    {
                        Clipboard.SetFileDropList(list);
                    }
                    catch (Exception)
                    {
                        // буфер занят другой программой — внутри проводника вставка всё равно сработает
                    }
                }

                AppLog.Info($"{(command == PaneCommand.Cut ? "Вырезано" : "Скопировано")} в буфер: {Count(picked)}");
                break;

            case PaneCommand.Paste:
                await PasteAsync(pane);
                break;

            case PaneCommand.TransferToOther:
                await TransferAsync(pane, picked, Other(pane).CurrentPath);
                break;

            case PaneCommand.Open:
            case PaneCommand.OpenWith:
                if (focused is null || focused.IsDirectory)
                {
                    return;
                }

                if (pane.Source is RemoteSource opened)
                {
                    await RemoteEdit.OpenAsync(opened, focused, command == PaneCommand.OpenWith);
                }
                else
                {
                    RemoteEdit.Launch(focused.FullPath, command == PaneCommand.OpenWith);
                }

                break;

            case PaneCommand.Properties:
                if (pane.Source is not { } source)
                {
                    return;
                }

                var targets = picked.Count > 0 ? picked : CurrentFolderEntry(pane) is { } folder ? [folder] : [];

                if (targets.Count > 0 && PropertiesDialog.Show(Window.GetWindow(this), source, targets))
                {
                    await pane.RefreshAsync();
                }

                break;

            case PaneCommand.Compare:
                await CompareAsync(pane);
                break;

            case PaneCommand.Run when pane.Source is RemoteSource remote && focused is { IsDirectory: false }:
                OpenCommandTab(remote.Server, $"{focused.Name}",
                    $"cd {SshService.ShellQuote(remote.Parent(focused.FullPath) ?? "/")} && ./{SshService.ShellQuote(focused.Name)}");
                break;

            case PaneCommand.CommandTabHere when pane.Source is RemoteSource remote:
                OpenCommandTab(remote.Server, Path.GetFileName(pane.CurrentPath.TrimEnd('/')) is { Length: > 0 } n ? n : "/",
                    $"cd {SshService.ShellQuote(pane.CurrentPath)} && ");
                break;

            case PaneCommand.SendToCopy when pane.Source is RemoteSource:
                var dir = focused is { IsDirectory: true } ? focused.FullPath : pane.CurrentPath;
                var copyTab = AppState.Current.ActiveCopyTab;
                copyTab.RemotePath = dir;
                AppLog.Success($"Путь назначения вкладки «{copyTab.Title}» в «Копировании»: {dir}");
                break;

            case PaneCommand.SendToDownload when pane.Source is RemoteSource remote && picked.Count > 0:
                var downloadTab = AppState.Current.ActiveDownloadTab;
                var added = downloadTab.AddDownloads(remote.Server, picked.Select(p => (p.FullPath, p.IsDirectory, p.Length, p.Modified)));
                AppLog.Success($"В список вкладки «{downloadTab.Title}» в «Скачивании» добавлено: {added}");
                break;
        }
    }

    /// <summary>Свойства без выделения — свойства открытой папки, как в проводнике.</summary>
    private static FsEntry? CurrentFolderEntry(FilePane pane) =>
        pane.CurrentPath.Length == 0 || pane.Source is null
            ? null
            : new FsEntry(Path.GetFileName(pane.CurrentPath.TrimEnd('/', '\\')) is { Length: > 0 } n ? n : pane.CurrentPath,
                pane.CurrentPath, true, 0, default);

    private async Task PasteAsync(FilePane pane)
    {
        var target = pane.CurrentPath;

        if (_clip is { } clip)
        {
            var sameSide = ReferenceEquals(clip.Pane.Source, pane.Source)
                || (clip.Pane.Source is RemoteSource a && pane.Source is RemoteSource b && a.Server == b.Server);

            if (sameSide)
            {
                await CopyWithinAsync(pane, clip.Items, target, clip.Cut);

                // вырезанное вставляется один раз — второй раз оно уже не там
                if (clip.Cut)
                {
                    _clip = null;
                }
            }
            else
            {
                await TransferAsync(clip.Pane, clip.Items, target, clip.Cut);
            }

            return;
        }

        // своего буфера нет — может, файлы скопировали в проводнике Windows
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().ToList();
                await PasteWindowsFilesAsync(pane, files, target, move: false);
            }
        }
        catch (Exception)
        {
            // буфер занят другой программой
        }
    }

    /// <summary>Два файла — по одному в каждой панели: размер, дата и SHA-256, итог плашкой.</summary>
    private async Task CompareAsync(FilePane pane)
    {
        var mine = pane.FocusedEntry;
        var theirs = Other(pane).FocusedEntry;

        if (mine is not { IsDirectory: false } || theirs is not { IsDirectory: false } || pane.Source is null || Other(pane).Source is null)
        {
            AppLog.Warn("Для сравнения выделите по файлу в каждой панели.");
            AppState.Current.RequestLog();
            return;
        }

        AppLog.Info($"Сравниваю {mine.Name} и {theirs.Name} …");

        var a = pane.Source.HashAsync(mine.FullPath, CancellationToken.None);
        var b = Other(pane).Source!.HashAsync(theirs.FullPath, CancellationToken.None);
        var (ha, hb) = (await a, await b);

        if (string.Equals(ha, hb, StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Success($"Файлы одинаковые: {mine.Name} = {theirs.Name} (SHA-256 {ha[..12]}…)");
        }
        else
        {
            AppLog.Warn($"Файлы отличаются: {mine.Name} ({TransferItem.Format(mine.Length)}, {mine.Modified:dd.MM.yyyy HH:mm}) "
                + $"и {theirs.Name} ({TransferItem.Format(theirs.Length)}, {theirs.Modified:dd.MM.yyyy HH:mm})");
        }
    }

    private static void OpenCommandTab(ServerEntry server, string title, string command)
    {
        var tab = AppState.Current.AddCommandTab(title, command, server);
        AppState.Current.ShowTab(tab);
    }
}
