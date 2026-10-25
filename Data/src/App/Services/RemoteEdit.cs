using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using MultiServerSync.Models;
using MultiServerSync.Views.Dialogs;

namespace MultiServerSync.Services;

/// <summary>
/// Открыть файл сервера программой Windows и залить обратно после сохранения — как в WinSCP,
/// только без вопросов. Файл скачивается во временную папку тем же скачиванием, что
/// у раздела «Скачивание», а уходит обратно той же заливкой, что у «Копирования»: замена
/// берёт права и владельца у прежнего файла на сервере, поэтому они не меняются.
/// </summary>
public static class RemoteEdit
{
    private static readonly List<EditedFile> Files = [];

    /// <summary>Временная папка правок: у каждого сервера своя, внутри — по папке на путь.</summary>
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiServerSync", "edit");

    /// <summary>Скачать (если ещё не скачан) и открыть. С выбором программы — окно «Открыть с помощью».</summary>
    public static async Task OpenAsync(RemoteSource source, FsEntry entry, bool chooseProgram)
    {
        var server = source.Server;
        var file = Files.FirstOrDefault(f => f.ServerId == server.Id && f.RemotePath == entry.FullPath && File.Exists(f.LocalPath));

        if (file is null)
        {
            var parent = SshService.ParentRemote(entry.FullPath);
            var dir = Path.Combine(Root, Safe(server.Id), Hash(parent));
            Directory.CreateDirectory(dir);

            AppLog.Info($"Открывается {entry.FullPath} …", server.Display);

            var stats = await DownloadService.DownloadAsync(
                server,
                AppState.Current.Settings,
                [entry.FullPath],
                dir,
                new UploadOptions(Overwrite: true, PreserveTimestamps: true, MatchByDate: false, MatchByHash: false),
                verify: false,
                line => AppLog.Debug(line, server.Display),
                CancellationToken.None);

            var local = Path.Combine(dir, entry.Name);

            if (stats.Failed > 0 || !File.Exists(local))
            {
                throw new IOException("файл не скачался — подробности в журнале");
            }

            file = new EditedFile(source, entry, local);
            Files.Add(file);
        }

        Launch(file.LocalPath, chooseProgram);
    }

    /// <summary>
    /// Открыть файл на этом компьютере. Файл без своей программы в Windows (.conf, .env, без
    /// расширения) открывается в Блокноте — окно «чем открыть» на каждый конфиг только мешает.
    /// </summary>
    public static void Launch(string path, bool chooseProgram)
    {
        if (chooseProgram)
        {
            Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {path}") { UseShellExecute = false });
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        }
    }

    /// <summary>Правки, которые ещё не ушли на сервер.</summary>
    public static int Pending => Files.Count(f => f.IsPending);

    /// <summary>
    /// Перед выходом: правленое, но не залитое — спросить и залить. false — выход отменили.
    /// Временная папка чистится, когда всё ушло.
    /// </summary>
    public static async Task<bool> BeforeExitAsync(Window owner)
    {
        var pending = Files.Where(f => f.IsPending).ToList();

        if (pending.Count > 0)
        {
            var names = string.Join("\n", pending.Select(f => f.RemotePath));
            var choice = ConfirmDialog.Choose(
                owner,
                "Залить правки на сервер перед выходом?",
                $"Эти файлы изменены, но ещё не ушли на сервер:\n{names}",
                "Залить и выйти",
                "Выйти без них");

            if (choice == 0)
            {
                return false;
            }

            if (choice == 1)
            {
                foreach (var file in pending)
                {
                    await file.UploadAsync(askIfServerChanged: true);
                }
            }
        }

        foreach (var file in Files)
        {
            file.Dispose();
        }

        Files.Clear();

        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (Exception)
        {
            // файл ещё открыт в редакторе — папка почистится при следующем выходе
        }

        return true;
    }

    private static string Safe(string text) =>
        new(text.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12].ToLowerInvariant();

    /// <summary>Один открытый файл: следит за сохранениями и заливает их.</summary>
    private sealed class EditedFile : IDisposable
    {
        private readonly RemoteSource _source;
        private readonly FileSystemWatcher _watcher;
        private readonly Timer _quiet;
        private long _remoteLength;
        private DateTime _remoteModified;
        private DateTime _syncedWrite;
        private bool _uploading;

        public EditedFile(RemoteSource source, FsEntry entry, string localPath)
        {
            _source = source;
            ServerId = source.Server.Id;
            RemotePath = entry.FullPath;
            LocalPath = localPath;
            _remoteLength = entry.Length;
            _remoteModified = entry.Modified;
            _syncedWrite = File.GetLastWriteTimeUtc(localPath);

            // редакторы сохраняют по-разному: прямо в файл или через временный и переименование
            _quiet = new Timer(_ => Application.Current?.Dispatcher.BeginInvoke(() => _ = UploadAsync(askIfServerChanged: true)));
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(localPath)!, Path.GetFileName(localPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            };

            FileSystemEventHandler changed = (_, _) => Touched();
            _watcher.Changed += changed;
            _watcher.Created += changed;
            _watcher.Renamed += (_, _) => Touched();
            _watcher.EnableRaisingEvents = true;
        }

        public string ServerId { get; }

        public string RemotePath { get; }

        public string LocalPath { get; }

        public bool IsPending { get; private set; }

        private void Touched()
        {
            IsPending = true;

            // ждём затишья: редактор пишет файл в несколько приёмов
            _quiet.Change(800, Timeout.Infinite);
        }

        public async Task UploadAsync(bool askIfServerChanged)
        {
            if (_uploading || !File.Exists(LocalPath))
            {
                return;
            }

            var written = File.GetLastWriteTimeUtc(LocalPath);

            if (written == _syncedWrite)
            {
                IsPending = false;
                return;
            }

            _uploading = true;
            var server = _source.Server;
            var parent = SshService.ParentRemote(RemotePath);
            var name = Path.GetFileName(RemotePath);

            try
            {
                // на сервере файл поменяли, пока его правили здесь, — молча затирать нельзя
                var now = (await _source.ListAsync(parent, refresh: true)).FirstOrDefault(e => e.Name == name);

                if (askIfServerChanged && now is not null
                    && (now.Length != _remoteLength || Math.Abs((now.Modified - _remoteModified).TotalSeconds) > 1)
                    && !ConfirmDialog.Ask(
                        Application.Current?.MainWindow,
                        $"На сервере {name} уже другой. Заменить?",
                        $"Файл {RemotePath} изменили на сервере после того, как его открыли здесь. "
                        + "Замена затрёт те изменения.",
                        "Заменить"))
                {
                    AppLog.Warn($"Правка {name} не залита: на сервере файл уже другой.", server.Display);
                    _syncedWrite = written;
                    IsPending = false;
                    return;
                }

                await SshService.UploadAsync(
                    server,
                    AppState.Current.Settings,
                    [new TransferItem(LocalPath)],
                    parent,
                    new UploadOptions(Overwrite: true, PreserveTimestamps: false, MatchByDate: false, MatchByHash: false),
                    new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    line => AppLog.Debug(line, server.Display),
                    CancellationToken.None);

                _syncedWrite = written;
                IsPending = false;

                if ((await _source.ListAsync(parent, refresh: true)).FirstOrDefault(e => e.Name == name) is { } after)
                {
                    _remoteLength = after.Length;
                    _remoteModified = after.Modified;
                }

                AppLog.Success($"Обновлён на сервере: {name}", server.Display);
            }
            catch (Exception ex)
            {
                AppLog.Error($"Правка {name} не залита: {ex.Message}", server.Display);
            }
            finally
            {
                _uploading = false;
            }
        }

        public void Dispose()
        {
            _watcher.Dispose();
            _quiet.Dispose();
        }
    }
}
