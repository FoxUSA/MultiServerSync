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
/// «Проводник»: слева этот компьютер, справа сервер вкладки (выбирается из названия правой
/// панели). Перенос между ними — та же заливка и то же скачивание, что у «Копирования»
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

        LocalPane.ShowTransfer(toRight: true, "Отмеченное — в открытую справа папку сервера. F5 — то же, а без галочек — строку под курсором");
        RemotePane.ShowTransfer(toRight: false, "Отмеченное — в открытую слева папку компьютера. F5 — то же, а без галочек — строку под курсором");
        RemotePane.EnableTitlePicker("Сменить сервер");
        RemotePane.TitleClicked += OnRemoteTitleClicked;

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

        // вкладку открыли снаружи («Проводник» в «Серверах») — папки у неё могли смениться,
        // даже если она и так открыта: перечитать при показе
        AppState.Current.TabShowRequested += workspace =>
        {
            if (!AppState.Current.ExplorerTabs.Contains(workspace))
            {
                return;
            }

            _reattach = true;

            if (IsVisible && ReferenceEquals(workspace, _tab))
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

            if (_reattach || !ReferenceEquals(_tab, AppState.Current.ActiveWorkspace))
            {
                _ = AttachAsync(AppState.Current.ActiveWorkspace);
            }
        };
    }

    /// <summary>Вкладку открыли снаружи — при показе перечитать её папки.</summary>
    private bool _reattach;

    // ── вкладка и сервер ──────────────────────────────────────────────────────

    private async Task AttachAsync(Workspace tab)
    {
        var version = ++_attachVersion;
        _reattach = false;

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

    /// <summary>Сервер сменили — правая половина переключается на него.</summary>
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
                "Выберите сервер кнопкой «Сервер» слева от пути — здесь откроются его папки.");
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
        LocalPane.CanTransfer = _remote is not null && LocalPane.SelectedEntries.Count > 0;
        RemotePane.CanTransfer = _remote is not null && RemotePane.SelectedEntries.Count > 0
            && LocalPane.CurrentPath.Length > 0;
    }

    // ── сервер правой панели ──────────────────────────────────────────────────

    /// <summary>
    /// Серверы — списком из названия правой панели: панели «Целевые сервера» у «Проводника»
    /// нет, всё место отдано файлам. Наборов несколько — серверы под названием своего набора.
    /// </summary>
    private void OnRemoteTitleClicked(FilePane pane, FrameworkElement anchor)
    {
        if (_tab is { } tab)
        {
            ServerMenu.Show(anchor, tab.ExplorerServer, tab.PickExplorerServer);
        }
    }

    // ── перенос ───────────────────────────────────────────────────────────────

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
            AppLog.Hint("Слева открыт «Этот компьютер» — откройте папку, куда скачивать.", LogLevel.Warning);
            return Task.CompletedTask;
        }

        if (_remote is not { } remote)
        {
            AppLog.Hint("Сервер не выбран — выберите его кнопкой слева от пути в правой панели.", LogLevel.Warning);
            return Task.CompletedTask;
        }

        if (move)
        {
            AppLog.Hint("Между компьютером и сервером только копирование: исходные файлы остаются на месте.");
        }

        if (tab.CopyBatch.IsRunning)
        {
            AppLog.Hint("Копирование встало в очередь — начнётся, когда закончится текущее.");
        }

        var server = remote.Server;
        var options = tab.CurrentOptions;
        var verify = tab.VerifyChecksums;
        var names = items.Select(i => i.Name).ToList();

        return Enqueue(tab, async () =>
        {
            AppLog.Section = AppSection.Explorer;
            ConfirmReplace ask = (_, paths) => AskReplace(tab, paths, targetDir);

            if (from.Source is LocalSource)
            {
                AppLog.Info($"Копирование на сервер: {Count(items)} → {targetDir}", server.Display);
                var payload = items.Select(i => new TransferItem(i.FullPath)).ToList();
                await tab.UploadCoreAsync(payload, targetDir, [server], options, verify, ask);
                remote.Invalidate(targetDir);
            }
            else
            {
                AppLog.Info($"Копирование с сервера: {Count(items)} → {targetDir}", server.Display);
                var plan = new Dictionary<ServerEntry, List<string>> { [server] = items.Select(i => i.FullPath).ToList() };
                await tab.DownloadCoreAsync(plan, targetDir, options, verify, ask);
            }

            AppState.Current.NotifyFinished(tab.CopyBatch);
            await RefreshShowingAsync(to, targetDir, names);
        });
    }

    /// <summary>
    /// Копирование перезапишет файлы в назначении — сначала спросить. Одинаковые при сверке
    /// по дате или сумме сюда не попадают: их копирование и так не трогает.
    /// </summary>
    private ReplaceChoice AskReplace(Workspace tab, IReadOnlyList<string> paths, string targetDir) =>
        Dispatcher.Invoke(() =>
        {
            if (tab.ReplaceWithoutAsking)
            {
                return ReplaceChoice.Replace;
            }

            // пути — от папки назначения: полный путь в окне не читается
            var root = targetDir.TrimEnd('/', '\\');
            var names = paths.Take(10).Select(p => p.Length > root.Length + 1 && p.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? p[(root.Length + 1)..] : p);
            var list = string.Join("\n", names) + (paths.Count > 10 ? $"\nи ещё {paths.Count - 10}" : string.Empty);

            var choice = ConfirmDialog.Choose(
                Window.GetWindow(this) ?? Application.Current.MainWindow,
                $"Заменить файлов: {paths.Count}?",
                $"{list}\n\nВ папке назначения они уже есть и будут перезаписаны.",
                "Заменить",
                "Пропустить их",
                "Больше не спрашивать, заменять сразу",
                out var dontAsk);

            if (choice == 1 && dontAsk)
            {
                tab.ReplaceWithoutAsking = true;
                AppLog.Hint("Замена без вопроса: вернуть вопрос — снять «не спрашивать перед заменой» в параметрах копирования.");
            }

            return choice switch
            {
                1 => ReplaceChoice.Replace,
                2 => ReplaceChoice.Skip,
                _ => ReplaceChoice.Cancel,
            };
        });

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

    /// <summary>
    /// Панель показывает папку, куда только что положили файлы, — перечитать. Выделение
    /// не трогается: галочки ставит человек.
    /// </summary>
    private static async Task RefreshShowingAsync(FilePane pane, string dir, IReadOnlyCollection<string> names)
    {
        if (pane.Source is null || !string.Equals(pane.Source.Normalize(pane.CurrentPath), pane.Source.Normalize(dir), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await pane.RefreshAsync();
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

        AppLog.Section = AppSection.Explorer;

        if (!source.IsRemote && targetDir.Length == 0)
        {
            AppLog.Hint("В «Этот компьютер» вставить нельзя — откройте папку.", LogLevel.Warning);
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

            if (clashes.Count > 0 && _tab is { ReplaceWithoutAsking: true })
            {
                replace = true;
            }
            else if (clashes.Count > 0)
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
        AppLog.Section = AppSection.Explorer;

        // отмеченное галочками, без них — строка под курсором; из меню правой кнопки — то, по чему щёлкнули
        var picked = pane.TargetEntries;
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

                AppLog.Hint($"{(command == PaneCommand.Cut ? "Вырезано" : "Скопировано")} в буфер: {Count(picked)}");
                break;

            case PaneCommand.Paste:
                await PasteAsync(pane);
                break;

            case PaneCommand.TransferToOther:
                await TransferAsync(pane, picked, Other(pane).CurrentPath);
                break;

            case PaneCommand.TransferOptions when _tab is { } tab:
                TransferOptionsDialog.Open(Window.GetWindow(this), tab);
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
                OpenCommand(remote.Server,
                    $"cd {SshService.ShellQuote(remote.Parent(focused.FullPath) ?? "/")} && ./{SshService.ShellQuote(focused.Name)}");
                break;

            case PaneCommand.CommandTabHere when pane.Source is RemoteSource remote:
                OpenCommand(remote.Server, $"cd {SshService.ShellQuote(pane.CurrentPath)} && ");
                break;

            case PaneCommand.SendToCopy when pane.Source is RemoteSource:
                var dir = focused is { IsDirectory: true } ? focused.FullPath : pane.CurrentPath;
                var copyTab = AppState.Current.ActiveCopyTab;
                copyTab.RemotePath = dir;
                AppLog.Success($"Путь назначения вкладки «{copyTab.Title}» в «Копировании»: {dir}");
                break;

            // строки встают ниже уже имеющихся в списке
            case PaneCommand.SendToDownload when pane.Source is RemoteSource remote && picked.Count > 0:
                var downloadTab = AppState.Current.MainDownloadTab;
                var added = downloadTab.AddDownloads(remote.Server, picked.Select(p => (p.FullPath, p.IsDirectory, p.Length, p.Modified)));
                AppLog.Success(added > 0
                    ? $"В список основной вкладки «{downloadTab.Title}» в «Скачивании» добавлено: {added}"
                    : $"Всё выбранное уже есть в списке «{downloadTab.Title}» в «Скачивании».");
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

    /// <summary>
    /// Два файла — по одному в каждой панели. Подробный отчёт (пути, размер, дата, SHA-256,
    /// в чём разница) — в терминал внизу, строкой сервера; в журнал — только итог.
    /// </summary>
    private async Task CompareAsync(FilePane pane)
    {
        var mine = pane.FocusedEntry;
        var theirs = Other(pane).FocusedEntry;

        if (mine is not { IsDirectory: false } || theirs is not { IsDirectory: false } || pane.Source is null || Other(pane).Source is null)
        {
            AppLog.Hint("Для сравнения поставьте курсор на файл в каждой панели.", LogLevel.Warning);
            return;
        }

        if (_tab is not { } tab || _remote is not { } remote)
        {
            AppLog.Hint("Сервер не выбран — выберите его кнопкой слева от пути в правой панели.", LogLevel.Warning);
            return;
        }

        // слева в отчёте всегда компьютер, справа сервер — как панели на экране
        var (local, localSource, server, serverSource) = pane.Source is LocalSource
            ? (mine, pane.Source, theirs, Other(pane).Source!)
            : (theirs, Other(pane).Source!, mine, pane.Source);

        await tab.CompareAsync(remote.Server, async (result, ct) =>
        {
            result.Message = "сравнение…";
            result.Append($"Сравнение файлов");
            result.Append($"  компьютер: {local.FullPath}");
            result.Append($"             {TransferItem.Format(local.Length)} · {local.Modified:dd.MM.yyyy HH:mm:ss}");
            result.Append($"  сервер:    {server.FullPath}");
            result.Append($"             {TransferItem.Format(server.Length)} · {server.Modified:dd.MM.yyyy HH:mm:ss}");

            var a = localSource.HashAsync(local.FullPath, ct);
            var b = serverSource.HashAsync(server.FullPath, ct);
            var (ha, hb) = (await a, await b);

            result.Append($"  SHA-256 компьютер: {ha}");
            result.Append($"  SHA-256 сервер:    {hb}");

            var same = string.Equals(ha, hb, StringComparison.OrdinalIgnoreCase);
            var differences = new List<string>();

            if (local.Length != server.Length)
            {
                differences.Add($"размер на {TransferItem.Format(Math.Abs(local.Length - server.Length))}");
            }

            if (Math.Abs((local.Modified - server.Modified).TotalSeconds) >= 2)
            {
                differences.Add(local.Modified > server.Modified ? "на компьютере новее" : "на сервере новее");
            }

            result.Append(same
                ? $"Итог: одинаковые по содержимому{(differences.Count > 0 ? " (" + string.Join(", ", differences) + ")" : string.Empty)}"
                : $"Итог: отличаются по содержимому{(differences.Count > 0 ? " — " + string.Join(", ", differences) : string.Empty)}");

            result.Message = same ? "одинаковые" : "отличаются";
            AppLog.Info($"Сравнение {local.Name} и {server.Name}: {(same ? "одинаковые" : "отличаются")}", remote.Server.Display);
        });
    }

    /// <summary>Команда — в основную вкладку «Команды» с этим сервером, и туда же переход.</summary>
    private static void OpenCommand(ServerEntry server, string command)
    {
        var tab = AppState.Current.CommandFor(server, command);
        AppState.Current.ShowTab(tab);
    }
}
