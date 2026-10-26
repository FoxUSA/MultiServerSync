using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using MultiServerSync.Core;
using MultiServerSync.Models;
using Renci.SshNet.Common;

namespace MultiServerSync.Services;

public enum FsKind
{
    Normal,

    /// <summary>Диск в «Этом компьютере».</summary>
    Drive,

    /// <summary>Быстрое место в «Этом компьютере»: Рабочий стол, Документы, Загрузки.</summary>
    Place,
}

/// <summary>Файл или папка — на компьютере или на сервере, одним видом для обеих панелей проводника.</summary>
/// <param name="Mode">Права числом, как в chmod; -1 — неизвестно (на компьютере всегда).</param>
/// <param name="Type">Своя подпись столбца «Тип» — для дисков и быстрых мест.</param>
public sealed record FsEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    long Length,
    DateTime Modified,
    string Permissions = "",
    int Mode = -1,
    string Owner = "",
    string Group = "",
    bool IsLink = false,
    string LinkTarget = "",
    bool IsHidden = false,
    FsKind Kind = FsKind.Normal,
    string? Type = null)
{
    /// <summary>Хоть у кого-то есть право запуска (только сервер).</summary>
    public bool IsExecutable => !IsDirectory && Mode >= 0 && (Mode & 0b001_001_001) != 0;
}

/// <summary>
/// Источник файлов для панели проводника — этот компьютер или сервер. Панель про разницу
/// не знает: и та и другая ходят по папкам, создают, переименовывают и удаляют одинаково.
/// </summary>
public interface IFileSource
{
    bool IsRemote { get; }

    /// <summary>Куда идти «домой» и откуда начинать, если папка не помнится.</summary>
    string HomePath { get; }

    /// <summary>Подготовка до первого чтения: на сервере — вход (после него известна домашняя папка).</summary>
    Task PrepareAsync();

    string Normalize(string path);

    /// <summary>Папка выше; null — выше некуда.</summary>
    string? Parent(string path);

    string Combine(string dir, string name);

    /// <summary>Части адреса для хлебных крошек: подпись и путь, начиная с корня.</summary>
    IReadOnlyList<(string Title, string Path)> Crumbs(string path);

    /// <summary>Путь словами для поля адреса.</summary>
    string Display(string path);

    bool IsFresh(string path);

    IReadOnlyList<FsEntry>? Cached(string path);

    Task<IReadOnlyList<FsEntry>> ListAsync(string path, bool refresh);

    /// <summary>Подпапки — заранее в кэш: щелчок по любой показывает её сразу.</summary>
    Task PrefetchAsync(IReadOnlyList<string> dirs);

    void Invalidate(string path);

    Task CreateFolderAsync(string path);

    Task CreateFileAsync(string path);

    Task RenameAsync(string from, string to);

    /// <summary>Удаляет; возвращает строки для журнала (что не вышло и почему).</summary>
    Task<IReadOnlyList<string>> DeleteAsync(IReadOnlyList<FsEntry> items);

    /// <summary>
    /// Копирование или перенос внутри этого же источника (с сервера на сервер, по компьютеру).
    /// <paramref name="replace"/> — заменять то, что уже лежит в папке назначения.
    /// </summary>
    Task<IReadOnlyList<string>> CopyHereAsync(IReadOnlyList<FsEntry> items, string targetDir, bool move, bool replace);

    /// <summary>Свободно на диске, где лежит папка; null — неизвестно.</summary>
    Task<long?> FreeSpaceAsync(string path);

    /// <summary>Поиск папок по маске вглубь от открытой.</summary>
    Task<IReadOnlyList<FsEntry>> FindFoldersAsync(string root, string mask, int depth, int limit, CancellationToken ct);

    /// <summary>Размер папки целиком и сколько в ней файлов и папок.</summary>
    Task<(long Bytes, int Files, int Dirs)> MeasureAsync(string path, CancellationToken ct);

    /// <summary>SHA-256 файла.</summary>
    Task<string> HashAsync(string path, CancellationToken ct);

    /// <summary>Файл целиком — для правки. Больше <paramref name="limit"/> байт — ошибка.</summary>
    Task<byte[]> ReadFileAsync(string path, long limit);

    /// <summary>
    /// Новое содержимое в тот же файл: не пересоздаётся, поэтому владелец, права и ссылки
    /// на него остаются как были.
    /// </summary>
    Task WriteFileAsync(string path, byte[] data);
}

// ── этот компьютер ─────────────────────────────────────────────────────────────

/// <summary>Файлы этого компьютера. Корень «Этот компьютер» — пустой путь: в нём диски и быстрые места.</summary>
public sealed class LocalSource : IFileSource
{
    public const string ComputerTitle = "Этот компьютер";

    private readonly Dictionary<string, IReadOnlyList<FsEntry>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _sync = new();

    public bool IsRemote => false;

    public string HomePath => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public Task PrepareAsync() => Task.CompletedTask;

    public string Normalize(string path)
    {
        var p = (path ?? string.Empty).Trim().Trim('"');

        if (p.Length == 0 || p.Equals(ComputerTitle, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (p == "~" || p.StartsWith("~\\", StringComparison.Ordinal) || p.StartsWith("~/", StringComparison.Ordinal))
        {
            p = HomePath + p[1..];
        }

        p = Environment.ExpandEnvironmentVariables(p).Replace('/', '\\');

        if (p.Length == 2 && p[1] == ':')
        {
            p += "\\";
        }

        try
        {
            p = Path.GetFullPath(p);
        }
        catch (Exception)
        {
            // кривой путь — пусть его разберёт чтение папки и скажет понятно
        }

        var root = Path.GetPathRoot(p);
        return string.Equals(p, root, StringComparison.OrdinalIgnoreCase) ? p : p.TrimEnd('\\');
    }

    public string? Parent(string path)
    {
        var p = Normalize(path);

        if (p.Length == 0)
        {
            return null;
        }

        return string.Equals(p, Path.GetPathRoot(p), StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : Path.GetDirectoryName(p) ?? string.Empty;
    }

    public string Combine(string dir, string name) => Path.Combine(dir, name);

    public IReadOnlyList<(string Title, string Path)> Crumbs(string path)
    {
        var p = Normalize(path);
        var crumbs = new List<(string, string)> { (ComputerTitle, string.Empty) };

        if (p.Length == 0)
        {
            return crumbs;
        }

        var root = Path.GetPathRoot(p) ?? p;
        crumbs.Add((root.TrimEnd('\\'), root));

        var built = root;

        foreach (var part in p[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            built = Path.Combine(built, part);
            crumbs.Add((part, built));
        }

        return crumbs;
    }

    public string Display(string path)
    {
        var p = Normalize(path);
        return p.Length == 0 ? ComputerTitle : p;
    }

    public bool IsFresh(string path) => false;

    public IReadOnlyList<FsEntry>? Cached(string path)
    {
        lock (_sync)
        {
            return _cache.GetValueOrDefault(Normalize(path));
        }
    }

    public async Task<IReadOnlyList<FsEntry>> ListAsync(string path, bool refresh)
    {
        var p = Normalize(path);
        var list = await Task.Run(() => p.Length == 0 ? Computer() : Read(p));

        lock (_sync)
        {
            _cache[p] = list;
        }

        return list;
    }

    private static IReadOnlyList<FsEntry> Read(string path)
    {
        var dir = new DirectoryInfo(path);

        if (!dir.Exists)
        {
            throw new DirectoryNotFoundException($"Папки {path} нет.");
        }

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };

        var items = new List<FsEntry>();

        // нет прав на саму папку — это видно сразу, а не пустым списком
        foreach (var info in dir.EnumerateFileSystemInfos("*", options))
        {
            var isDir = (info.Attributes & FileAttributes.Directory) != 0;
            var link = info.LinkTarget;

            items.Add(new FsEntry(
                info.Name,
                info.FullName,
                isDir,
                info is FileInfo file ? SafeLength(file) : 0,
                info.LastWriteTime,
                IsLink: link is not null,
                LinkTarget: link ?? string.Empty,
                IsHidden: (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0));
        }

        return items;
    }

    private static long SafeLength(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>«Этот компьютер»: быстрые места и диски, как в левой части проводника.</summary>
    private static IReadOnlyList<FsEntry> Computer()
    {
        var items = new List<FsEntry>();

        void Place(string title, Environment.SpecialFolder folder, string? fallback = null)
        {
            var path = Environment.GetFolderPath(folder);

            if (path.Length == 0 && fallback is not null)
            {
                path = fallback;
            }

            if (path.Length > 0 && Directory.Exists(path))
            {
                items.Add(new FsEntry(title, path, true, 0, default, Kind: FsKind.Place, Type: "Папка"));
            }
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Place("Рабочий стол", Environment.SpecialFolder.DesktopDirectory);
        Place("Документы", Environment.SpecialFolder.MyDocuments);
        Place("Загрузки", Environment.SpecialFolder.UserProfile, Path.Combine(profile, "Downloads"));

        // у «Загрузок» нет своего SpecialFolder — берём папку в профиле
        if (items.LastOrDefault() is { Name: "Загрузки" } downloads)
        {
            items[^1] = downloads with { FullPath = Path.Combine(profile, "Downloads") };

            if (!Directory.Exists(items[^1].FullPath))
            {
                items.RemoveAt(items.Count - 1);
            }
        }

        Place("Изображения", Environment.SpecialFolder.MyPictures);
        Place("Личная папка", Environment.SpecialFolder.UserProfile);

        foreach (var drive in DriveInfo.GetDrives())
        {
            string label;
            long total = 0;
            long free = 0;

            try
            {
                label = drive.IsReady && drive.VolumeLabel.Length > 0 ? drive.VolumeLabel : DriveTitle(drive.DriveType);
                total = drive.IsReady ? drive.TotalSize : 0;
                free = drive.IsReady ? drive.AvailableFreeSpace : 0;
            }
            catch (Exception)
            {
                label = DriveTitle(drive.DriveType);
            }

            var letter = drive.Name.TrimEnd('\\');
            var type = total > 0
                ? $"{DriveTitle(drive.DriveType)} · свободно {TransferItem.Format(free)} из {TransferItem.Format(total)}"
                : DriveTitle(drive.DriveType);

            items.Add(new FsEntry($"{label} ({letter})", drive.Name, true, total, default, Kind: FsKind.Drive, Type: type));
        }

        return items;
    }

    private static string DriveTitle(DriveType type) => type switch
    {
        DriveType.Removable => "Съёмный диск",
        DriveType.Network => "Сетевой диск",
        DriveType.CDRom => "Дисковод",
        DriveType.Ram => "Диск в памяти",
        _ => "Локальный диск",
    };

    public Task PrefetchAsync(IReadOnlyList<string> dirs) => Task.CompletedTask;

    public void Invalidate(string path)
    {
        lock (_sync)
        {
            _cache.Remove(Normalize(path));
        }
    }

    public Task CreateFolderAsync(string path) => Task.Run(() =>
    {
        if (Directory.Exists(path) || File.Exists(path))
        {
            throw new IOException($"{Path.GetFileName(path)} уже есть в этой папке.");
        }

        Directory.CreateDirectory(path);
    });

    public Task CreateFileAsync(string path) => Task.Run(() =>
    {
        using var _ = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
    });

    public Task RenameAsync(string from, string to) => Task.Run(() =>
    {
        if (Directory.Exists(from))
        {
            Directory.Move(from, to);
        }
        else
        {
            File.Move(from, to);
        }
    });

    public async Task<IReadOnlyList<string>> DeleteAsync(IReadOnlyList<FsEntry> items)
    {
        var done = await ShellFileOps.RecycleAsync(Application.Current?.MainWindow, items.Select(i => i.FullPath).ToList());
        return done ? [] : ["удаление прервано или не завершено"];
    }

    public async Task<IReadOnlyList<string>> CopyHereAsync(IReadOnlyList<FsEntry> items, string targetDir, bool move, bool replace)
    {
        var owner = Application.Current?.MainWindow;
        var paths = items.Select(i => i.FullPath).ToList();

        // в ту же папку — копия рядом с именем «… — копия», как в проводнике
        var sameFolder = items.All(i => string.Equals(Parent(i.FullPath), Normalize(targetDir), StringComparison.OrdinalIgnoreCase));

        if (move && sameFolder)
        {
            return [];
        }

        var done = move
            ? await ShellFileOps.MoveAsync(owner, paths, targetDir)
            : await ShellFileOps.CopyAsync(owner, paths, targetDir, renameOnCollision: sameFolder);

        return done ? [] : [(move ? "перенос" : "копирование") + " прервано или не завершено"];
    }

    public Task<long?> FreeSpaceAsync(string path) => Task.Run<long?>(() =>
    {
        var p = Normalize(path);

        if (p.Length == 0 || Path.GetPathRoot(p) is not { Length: > 0 } root || root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception)
        {
            return null;
        }
    });

    public Task<IReadOnlyList<FsEntry>> FindFoldersAsync(string root, string mask, int depth, int limit, CancellationToken ct) =>
        Task.Run<IReadOnlyList<FsEntry>>(() =>
        {
            var found = new List<FsEntry>();
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = depth,
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
            };

            foreach (var dir in new DirectoryInfo(root).EnumerateDirectories("*", options))
            {
                ct.ThrowIfCancellationRequested();

                if (!NameMask.Matches(dir.Name, mask))
                {
                    continue;
                }

                found.Add(new FsEntry(dir.Name, dir.FullName, true, 0, dir.LastWriteTime,
                    IsHidden: (dir.Attributes & FileAttributes.Hidden) != 0));

                if (found.Count >= limit)
                {
                    break;
                }
            }

            return found;
        }, ct);

    public Task<(long Bytes, int Files, int Dirs)> MeasureAsync(string path, CancellationToken ct) => Task.Run(() =>
    {
        long bytes = 0;
        var files = 0;
        var dirs = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };

        foreach (var info in new DirectoryInfo(path).EnumerateFileSystemInfos("*", options))
        {
            ct.ThrowIfCancellationRequested();

            if (info is FileInfo file)
            {
                files++;
                bytes += SafeLength(file);
            }
            else
            {
                dirs++;
            }
        }

        return (bytes, files, dirs);
    }, ct);

    public Task<string> HashAsync(string path, CancellationToken ct) =>
        Task.Run(() => SshService.ComputeLocalSha256(path), ct);

    public Task<byte[]> ReadFileAsync(string path, long limit) => Task.Run(() =>
    {
        var size = new FileInfo(path).Length;
        return size > limit ? throw new IOException(TextFile.TooBig(size)) : File.ReadAllBytes(path);
    });

    public Task WriteFileAsync(string path, byte[] data) => Task.Run(() =>
    {
        // Truncate, а не Create: тот же файл, его права и атрибуты не трогаются
        using var stream = new FileStream(path, FileMode.Truncate, FileAccess.Write, FileShare.Read);
        stream.Write(data);
    });
}

// ── сервер ─────────────────────────────────────────────────────────────────────

/// <summary>Свойства объекта на сервере — то, что показывает окно «Свойства».</summary>
public sealed record RemoteStat(
    string Path,
    int Mode,
    string Owner,
    string Group,
    int Uid,
    int Gid,
    long Size,
    DateTime Modified,
    DateTime Accessed,
    string Kind);

/// <summary>
/// Файлы сервера поверх <see cref="RemoteSession"/>: чтение папок из её кэша, служебные
/// операции — одной командой на всё выделенное. Права и владельцев существующего никто,
/// кроме окна «Свойства», не трогает.
/// </summary>
public sealed class RemoteSource(RemoteSession session) : IFileSource
{
    public RemoteSession Session { get; } = session;

    public ServerEntry Server => Session.Server;

    public bool IsRemote => true;

    public string HomePath => Session.Home ?? "/";

    public Task PrepareAsync() => Session.ConnectAsync();

    public string Normalize(string path)
    {
        var p = (path ?? string.Empty).Trim();

        if (p.Length == 0 || p == "~")
        {
            return HomePath;
        }

        if (p.StartsWith("~/", StringComparison.Ordinal))
        {
            p = HomePath.TrimEnd('/') + p[1..];
        }

        return SshService.NormalizeRemote(p);
    }

    public string? Parent(string path)
    {
        var p = Normalize(path);
        return p == "/" ? null : SshService.ParentRemote(p);
    }

    public string Combine(string dir, string name) => SshService.CombineRemote(dir, name);

    public IReadOnlyList<(string Title, string Path)> Crumbs(string path)
    {
        var crumbs = new List<(string, string)> { ("/", "/") };
        var built = string.Empty;

        foreach (var part in Normalize(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            built += "/" + part;
            crumbs.Add((part, built));
        }

        return crumbs;
    }

    public string Display(string path) => Normalize(path);

    public bool IsFresh(string path) => Session.IsFresh(Normalize(path));

    public IReadOnlyList<FsEntry>? Cached(string path) =>
        Session.Cached(Normalize(path)) is { } items ? items.Select(ToEntry).ToList() : null;

    public async Task<IReadOnlyList<FsEntry>> ListAsync(string path, bool refresh)
    {
        var target = Normalize(path);

        if (!Session.IsFresh(target))
        {
            await Session.ConnectAsync();
        }

        return (await Session.ListAsync(target, refresh)).Select(ToEntry).ToList();
    }

    private static FsEntry ToEntry(RemoteEntry e) => new(
        e.Name,
        e.FullPath,
        e.IsDirectory,
        e.Length,
        e.Modified,
        e.Permissions,
        e.Mode,
        e.Owner,
        e.Group,
        e.IsLink,
        e.LinkTarget,
        IsHidden: e.Name.StartsWith('.'));

    public bool? HasSubdirs(string path) => Session.HasSubdirs(Normalize(path));

    public Task PrefetchAsync(IReadOnlyList<string> dirs) => Session.PrefetchFastAsync(dirs);

    public void Invalidate(string path) => Session.Invalidate(Normalize(path));

    public async Task CreateFolderAsync(string path)
    {
        await Session.CreateDirectoryAsync(path);
        Session.Invalidate(SshService.ParentRemote(path));
    }

    public async Task CreateFileAsync(string path)
    {
        var problems = new List<string>();
        await Session.SftpAsync(sftp =>
        {
            SshService.CreateEmptyFile(sftp, path, problems.Add);
            return true;
        });

        Session.Invalidate(SshService.ParentRemote(path));

        foreach (var line in problems)
        {
            AppLog.Warn(line, Server.Display);
        }
    }

    public async Task RenameAsync(string from, string to)
    {
        await Session.SftpAsync(sftp =>
        {
            if (sftp.Exists(to))
            {
                throw new IOException($"{Path.GetFileName(to)} уже есть в этой папке.");
            }

            sftp.RenameFile(from, to);
            return true;
        });

        Session.Invalidate(SshService.ParentRemote(from));
        Session.Invalidate(SshService.ParentRemote(to));
        Session.Invalidate(from);
    }

    /// <summary>
    /// Удаление: файл и ссылка — только они сами, папка — со всем содержимым. Путь всегда без «/»
    /// на конце: иначе rm пошёл бы в папку, на которую указывает ссылка.
    /// </summary>
    public async Task<IReadOnlyList<string>> DeleteAsync(IReadOnlyList<FsEntry> items)
    {
        var paths = items.Select(i => SshService.NormalizeRemote(i.FullPath)).Where(p => p.Length > 1).Distinct().ToList();

        if (paths.Count == 0)
        {
            return ["корень «/» не удаляется"];
        }

        var script = new StringBuilder("export LC_ALL=C\n");

        for (var i = 0; i < paths.Count; i++)
        {
            script.Append("rm -rf -- ").Append(SshService.ShellQuote(paths[i])).Append(" || echo 'MSS-FAIL ").Append(i).Append("'\n");
        }

        var (output, error) = await Session.ExecAsync(script.ToString());

        foreach (var parent in paths.Select(SshService.ParentRemote).Distinct())
        {
            Session.Invalidate(parent);
        }

        foreach (var path in paths)
        {
            Session.Invalidate(path);
        }

        return Failures(output, error, paths, "не удалено");
    }

    public async Task<IReadOnlyList<string>> CopyHereAsync(IReadOnlyList<FsEntry> items, string targetDir, bool move, bool replace)
    {
        var target = Normalize(targetDir);
        var existing = (await ListAsync(target, refresh: true)).ToDictionary(e => e.Name, StringComparer.Ordinal);
        var notes = new List<string>();
        var script = new StringBuilder("export LC_ALL=C\necho \"MSS-UID $(id -u) $(id -un)\"\n");
        var jobs = new List<string>();

        foreach (var item in items)
        {
            var source = SshService.NormalizeRemote(item.FullPath);

            if (item.IsDirectory && !item.IsLink && (target == source || target.StartsWith(source + "/", StringComparison.Ordinal)))
            {
                notes.Add($"{item.Name}: папку нельзя {(move ? "перенести" : "скопировать")} внутрь неё самой");
                continue;
            }

            var sameFolder = SshService.ParentRemote(source) == target;

            if (move && sameFolder)
            {
                continue;
            }

            var name = sameFolder ? CopyName(item.Name, existing.Keys) : item.Name;
            var dest = Combine(target, name);
            var q = SshService.ShellQuote(source);
            var d = SshService.ShellQuote(dest);
            string command;

            if (existing.TryGetValue(name, out var there))
            {
                if (!replace)
                {
                    notes.Add($"{name}: уже есть в {target}, пропущено");
                    continue;
                }

                if (there.IsDirectory != item.IsDirectory)
                {
                    notes.Add($"{name}: в {target} есть {(there.IsDirectory ? "папка" : "файл")} с таким именем — не заменяется");
                    continue;
                }

                if (item.IsDirectory && move)
                {
                    notes.Add($"{name}: папка с таким именем уже есть — перенос со слиянием не делается, скопируйте и удалите");
                    continue;
                }

                // папка поверх папки — содержимое сливается, лишнее в назначении остаётся
                command = item.IsDirectory && !item.IsLink
                    ? $"cp -a -- {SshService.ShellQuote(source + "/.")} {SshService.ShellQuote(dest + "/")}"
                    : move ? $"mv -f -- {q} {d}" : $"cp -a -- {q} {d}";
            }
            else
            {
                command = move ? $"mv -- {q} {d}" : $"cp -a -- {q} {d}";
            }

            script.Append(command).Append(" || echo 'MSS-FAIL ").Append(jobs.Count).Append("'\n");
            jobs.Add(item.Name);

            if (move)
            {
                Session.Invalidate(SshService.ParentRemote(source));
            }
        }

        if (jobs.Count == 0)
        {
            return notes;
        }

        var (output, error) = await Session.ExecAsync(script.ToString());
        Session.Invalidate(target);

        notes.AddRange(Failures(output, error, jobs, move ? "не перенесено" : "не скопировано"));

        // копия под не-root достаётся нам: исходный владелец сохраняется только у root
        if (!move && Uid(output) is { Id: not 0, Name: var me } && items.Any(i => i.Owner.Length > 0 && i.Owner != me))
        {
            notes.Add($"владелец копий — {me}: сохранить исходного владельца может только root");
        }

        return notes;
    }

    /// <summary>«имя — копия», «имя — копия (2)» — как в проводнике, расширение остаётся в конце.</summary>
    private static string CopyName(string name, IEnumerable<string> taken)
    {
        var set = taken.ToHashSet(StringComparer.Ordinal);
        var dot = name.LastIndexOf('.');
        var (stem, ext) = dot > 0 ? (name[..dot], name[dot..]) : (name, string.Empty);

        for (var n = 1; ; n++)
        {
            var candidate = n == 1 ? $"{stem} — копия{ext}" : $"{stem} — копия ({n}){ext}";

            if (!set.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static (int Id, string Name)? Uid(string output)
    {
        var line = output.Split('\n').FirstOrDefault(l => l.StartsWith("MSS-UID ", StringComparison.Ordinal));
        var parts = line?.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return parts is { Length: >= 3 } && int.TryParse(parts[1], out var id) ? (id, parts[2]) : null;
    }

    /// <summary>Что не вышло: строки по меткам MSS-FAIL и первая строка ответа сервера как причина.</summary>
    private static IReadOnlyList<string> Failures(string output, string error, IReadOnlyList<string> names, string what)
    {
        var failed = output.Split('\n')
            .Where(l => l.StartsWith("MSS-FAIL ", StringComparison.Ordinal))
            .Select(l => int.TryParse(l[9..].Trim(), out var i) ? i : -1)
            .Where(i => i >= 0 && i < names.Count)
            .ToList();

        if (failed.Count == 0)
        {
            return [];
        }

        var why = error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return failed.Select(i => $"{what}: {names[i]}" + (why is null ? string.Empty : $" — {why}")).ToList();
    }

    public async Task<long?> FreeSpaceAsync(string path)
    {
        try
        {
            var (output, _) = await Session.ExecAsync(
                $"LC_ALL=C df -Pk -- {SshService.ShellQuote(Normalize(path))} 2>/dev/null | tail -n 1\n");
            var parts = output.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 4 && long.TryParse(parts[3], out var kb) ? kb * 1024 : null;
        }
        catch (Exception ex) when (ex is SshException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<FsEntry>> FindFoldersAsync(string root, string mask, int depth, int limit, CancellationToken ct)
    {
        var patterns = NameMask.Patterns(mask);

        if (patterns.Count == 0)
        {
            return [];
        }

        var names = string.Join(" -o ", patterns.Select(p => "-iname " + SshService.ShellQuote(p)));
        var script =
            "export LC_ALL=C\n"
            + $"find {SshService.ShellQuote(Normalize(root))} -mindepth 1 -maxdepth {depth} -type d \\( {names} \\) -print0 2>/dev/null"
            + $" | head -c {4 * 1024 * 1024}\n";

        var (output, _) = await Session.ExecAsync(script, ct);

        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Take(limit)
            .Select(p => new FsEntry(Path.GetFileName(p), SshService.NormalizeRemote(p), true, 0, default,
                IsHidden: Path.GetFileName(p).StartsWith('.')))
            .ToList();
    }

    public async Task<(long Bytes, int Files, int Dirs)> MeasureAsync(string path, CancellationToken ct)
    {
        var q = SshService.ShellQuote(Normalize(path));

        // du -sb есть только у GNU; где его нет — в килобайтах
        var script =
            "export LC_ALL=C\n"
            + $"b=$(du -sb -- {q} 2>/dev/null) && echo \"B $b\" || echo \"K $(du -sk -- {q} 2>/dev/null)\"\n"
            + $"echo \"F $(find {q} -type f 2>/dev/null | wc -l)\"\n"
            + $"echo \"D $(find {q} -mindepth 1 -type d 2>/dev/null | wc -l)\"\n";

        var (output, _) = await Session.ExecAsync(script, ct);
        long bytes = 0;
        var files = 0;
        var dirs = 0;

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length < 2 || !long.TryParse(parts[1], out var number))
            {
                continue;
            }

            switch (parts[0])
            {
                case "B":
                    bytes = number;
                    break;
                case "K":
                    bytes = number * 1024;
                    break;
                case "F":
                    files = (int)number;
                    break;
                case "D":
                    dirs = (int)number;
                    break;
            }
        }

        return (bytes, files, dirs);
    }

    public async Task<string> HashAsync(string path, CancellationToken ct)
    {
        var (output, error) = await Session.ExecAsync($"sha256sum -- {SshService.ShellQuote(Normalize(path))}\n", ct);
        var hash = output.Split(' ', 2)[0].Trim();

        return hash.Length == 64
            ? hash
            : throw new IOException("сервер не посчитал сумму: " + (error.Split('\n').FirstOrDefault()?.Trim() ?? "нет ответа"));
    }

    public Task<byte[]> ReadFileAsync(string path, long limit) => Session.SftpAsync(sftp =>
    {
        var target = Normalize(path);
        var size = sftp.GetAttributes(target).Size;
        return size > limit ? throw new IOException(TextFile.TooBig(size)) : sftp.ReadAllBytes(target);
    });

    /// <summary>
    /// Сначала целиком во временный файл в домашней папке (права 600 — до того, как в нём что-то
    /// появится), потом на сервере <c>cat &gt;</c> поверх файла. Оборвётся связь — недописанным
    /// останется временный файл, а не правленый конфиг; <c>cat</c> пишет в тот же файл, поэтому
    /// владелец и права у него прежние.
    /// </summary>
    public async Task WriteFileAsync(string path, byte[] data)
    {
        var target = Normalize(path);
        var temp = SshService.CombineRemote(Session.Home ?? "/tmp", ".mss-edit-" + Guid.NewGuid().ToString("N"));

        await Session.SftpAsync(sftp =>
        {
            // Create, а не CreateNew: после обрыва сессия повторяет это же с тем же именем
            using var stream = sftp.Open(temp, FileMode.Create, FileAccess.Write);
            sftp.ChangePermissions(temp, 0b110_000_000);
            stream.Write(data, 0, data.Length);
            return true;
        });

        var script = $"t={SshService.ShellQuote(temp)}\n"
            + $"cat -- \"$t\" > {SshService.ShellQuote(target)}\n"
            + "rc=$?\nrm -f -- \"$t\"\n[ $rc -eq 0 ] || echo MSS-FAIL\n";
        var (output, error) = await Session.ExecAsync(script);

        Session.Invalidate(SshService.ParentRemote(target));

        if (output.Contains("MSS-FAIL", StringComparison.Ordinal))
        {
            var reason = error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            throw new IOException(reason?.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) == true
                ? "нет прав на запись в этот файл"
                : reason ?? "сервер не записал файл");
        }
    }

    // ── свойства: права и владелец меняются только здесь ──────────────────────

    /// <summary>Права, владелец, размер и даты — одной командой на все пути.</summary>
    public async Task<IReadOnlyList<RemoteStat>> StatAsync(IReadOnlyList<string> paths)
    {
        var script = new StringBuilder("export LC_ALL=C\n");

        foreach (var path in paths)
        {
            script.Append("printf '%s\\t' ").Append(SshService.ShellQuote(path))
                .Append("; stat -c '%a\t%U\t%G\t%u\t%g\t%s\t%Y\t%X\t%F' -- ")
                .Append(SshService.ShellQuote(path)).Append(" 2>/dev/null || echo\n");
        }

        var (output, _) = await Session.ExecAsync(script.ToString());
        var stats = new List<RemoteStat>();

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');

            if (parts.Length < 10)
            {
                continue;
            }

            stats.Add(new RemoteStat(
                parts[0],
                Octal(parts[1]),
                parts[2],
                parts[3],
                int.TryParse(parts[4], out var uid) ? uid : -1,
                int.TryParse(parts[5], out var gid) ? gid : -1,
                long.TryParse(parts[6], out var size) ? size : 0,
                Unix(parts[7]),
                Unix(parts[8]),
                parts[9]));
        }

        return stats;
    }

    /// <summary>
    /// Права числом. Для папок со «вложенными» — отдельно папкам и файлам: одинаковые биты
    /// на всё дерево сделали бы файлы исполняемыми или закрыли бы вход в папки. Ссылки
    /// не трогаются — chmod по ссылке менял бы то, на что она указывает.
    /// </summary>
    public async Task<IReadOnlyList<string>> ChmodAsync(IReadOnlyList<string> paths, int mode, int? dirMode, int? fileMode)
    {
        var script = new StringBuilder("export LC_ALL=C\n");
        var names = new List<string>();

        foreach (var path in paths)
        {
            var q = SshService.ShellQuote(path);

            if (dirMode is { } dm || fileMode is not null)
            {
                if (dirMode is { } d)
                {
                    script.Append($"find {q} -type d -exec chmod {ToOctal(d)} {{}} + || echo 'MSS-FAIL {names.Count}'\n");
                    names.Add(path);
                }

                if (fileMode is { } f)
                {
                    script.Append($"find {q} -type f -exec chmod {ToOctal(f)} {{}} + || echo 'MSS-FAIL {names.Count}'\n");
                    names.Add(path);
                }
            }
            else
            {
                script.Append($"chmod {ToOctal(mode)} -- {q} || echo 'MSS-FAIL {names.Count}'\n");
                names.Add(path);
            }
        }

        var (output, error) = await Session.ExecAsync(script.ToString());
        InvalidateAround(paths);
        return Failures(output, error, names, "права не изменены");
    }

    /// <summary>Владелец и группа; пустое — не менять. Сменить владельца может только root.</summary>
    public async Task<IReadOnlyList<string>> ChownAsync(IReadOnlyList<string> paths, string owner, string group, bool recursive)
    {
        var who = owner.Length > 0 && group.Length > 0 ? $"{owner}:{group}" : owner.Length > 0 ? owner : $":{group}";
        var script = new StringBuilder("export LC_ALL=C\n");

        for (var i = 0; i < paths.Count; i++)
        {
            script.Append("chown ").Append(recursive ? "-R " : string.Empty).Append(SshService.ShellQuote(who))
                .Append(" -- ").Append(SshService.ShellQuote(paths[i])).Append($" || echo 'MSS-FAIL {i}'\n");
        }

        var (output, error) = await Session.ExecAsync(script.ToString());
        InvalidateAround(paths);
        return Failures(output, error, paths, "владелец не изменён");
    }

    /// <summary>Пользователи и группы сервера — для выпадающих списков в «Свойствах».</summary>
    public async Task<(IReadOnlyList<string> Users, IReadOnlyList<string> Groups, bool IsRoot)> AccountsAsync()
    {
        var script =
            "export LC_ALL=C\n"
            + "echo \"MSS-UID $(id -u) $(id -un)\"\n"
            + "(getent passwd 2>/dev/null || cat /etc/passwd) | cut -d: -f1\n"
            + "echo MSS-SPLIT\n"
            + "(getent group 2>/dev/null || cat /etc/group) | cut -d: -f1\n";

        var (output, _) = await Session.ExecAsync(script);
        var users = new List<string>();
        var groups = new List<string>();
        var target = users;

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("MSS-UID", StringComparison.Ordinal))
            {
                continue;
            }

            if (line == "MSS-SPLIT")
            {
                target = groups;
                continue;
            }

            target.Add(line);
        }

        users.Sort(StringComparer.Ordinal);
        groups.Sort(StringComparer.Ordinal);
        return (users.Distinct().ToList(), groups.Distinct().ToList(), Uid(output) is { Id: 0 });
    }

    private void InvalidateAround(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            Session.Invalidate(path);
            Session.Invalidate(SshService.ParentRemote(path));
        }
    }

    public static string ToOctal(int mode) => Convert.ToString(mode & 0xFFF, 8).PadLeft(3, '0');

    private static int Octal(string text)
    {
        try
        {
            return Convert.ToInt32(text.Trim(), 8);
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static DateTime Unix(string text) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) && s > 0
            ? DateTimeOffset.FromUnixTimeSeconds(s).LocalDateTime
            : default;
}
