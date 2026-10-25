using System.Collections.Concurrent;
using System.Formats.Tar;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using MultiServerSync.Core;
using MultiServerSync.Models;
using Renci.SshNet;

namespace MultiServerSync.Services;

/// <summary>Итог скачивания с одного сервера.</summary>
public sealed record DownloadStats(
    int Received,
    int Skipped,
    int UpToDate,
    int Dirs,
    long Bytes,
    int Excluded,
    int Links,
    int Mismatches,
    int Failed,
    IReadOnlyList<string> Files);

/// <summary>
/// Скачивание с сервера — зеркало копирования. Опись — одной командой find; что качать,
/// решается так же (все / только отсутствующие / по дате / по сумме); мелочь едет одним
/// сжатым tar-потоком, крупное — по SFTP в несколько подключений. Каждый файл пишется
/// во временный рядом и встаёт на место целиком: оборвалось — старый файл цел.
/// </summary>
public static class DownloadService
{
    /// <summary>Файлы меньше этого едут пачкой tar, крупные — каждый своим SFTP.</summary>
    private const long SmallFile = 1024 * 1024;

    /// <summary>С какого числа мелких файлов пачка окупается.</summary>
    private const int BatchFrom = 4;

    /// <summary>Предел одной пачки по числу файлов и объёму.</summary>
    private const int BatchFiles = 2000;

    private const long BatchBytes = 64L * 1024 * 1024;

    /// <summary>Сколько подключений SFTP везут крупные файлы одновременно.</summary>
    private const int BigFileConnections = 4;

    /// <summary>Запас при сравнении дат: у SFTP время с точностью до секунды.</summary>
    private const double DateSlackSeconds = 2;

    private sealed record Entry(string Remote, string Relative, bool IsDirectory, long Size, double MTime);

    private sealed record Job(Entry Entry, string Local);

    public static Task<DownloadStats> DownloadAsync(
        ServerEntry server,
        AppSettings settings,
        IReadOnlyList<string> remotePaths,
        string localRoot,
        UploadOptions options,
        bool verify,
        Action<string> report,
        CancellationToken ct,
        Action<long, long>? progress = null)
    {
        return Task.Run(
            () =>
            {
                var meter = new TransferMeter(progress, ct);

                using var ssh = SshService.NewSsh(server, settings);
                using var sftp = SshService.NewSftp(server, settings);
                SshService.ConnectBoth(ssh, sftp);

                var exclude = ExcludeRules.Parse(options.ExcludeMasks);
                var entries = new List<Entry>();
                var excluded = 0;
                var links = 0;

                foreach (var raw in remotePaths)
                {
                    ct.ThrowIfCancellationRequested();

                    var path = SshService.NormalizeRemote(raw).TrimEnd('/');

                    if (path.Length == 0)
                    {
                        report("корень «/» целиком не скачивается — укажите папку");
                        continue;
                    }

                    var listed = List(ssh, sftp, path, report, ref links, ct);
                    entries.AddRange(Filter(listed, exclude, ref excluded));
                }

                entries = Unique(entries, localRoot, report);

                if (excluded > 0)
                {
                    report($"исключено по маскам: {excluded}");
                }

                if (links > 0)
                {
                    report($"символьные ссылки не скачиваются: {links}");
                }

                Directory.CreateDirectory(localRoot);

                // папки — сразу: пустые тоже должны появиться
                var dirs = 0;

                foreach (var dir in entries.Where(e => e.IsDirectory))
                {
                    var local = LocalPath(localRoot, dir.Relative);

                    if (!Directory.Exists(local))
                    {
                        Directory.CreateDirectory(local);
                        dirs++;
                    }
                }

                var (jobs, skipped, upToDate) = Decide(ssh, entries.Where(e => !e.IsDirectory).ToList(), localRoot, options, report, ct);

                var received = new ConcurrentBag<Job>();
                long bytes = 0;

                meter.Total = jobs.Sum(j => j.Entry.Size);
                meter.Finish(); // 0 из N — полоса появляется сразу, а не с первым куском

                var small = jobs.Where(j => j.Entry.Size < SmallFile).ToList();
                var big = jobs.Where(j => j.Entry.Size >= SmallFile).ToList();

                // мелочи мало — пачка не окупится, едет по SFTP вместе с крупными
                if (small.Count < BatchFrom)
                {
                    big.AddRange(small);
                    small.Clear();
                }

                foreach (var batch in Batches(small))
                {
                    ct.ThrowIfCancellationRequested();

                    var left = ReceiveTar(ssh, batch, options.PreserveTimestamps, received, meter, ref bytes, ct);

                    // tar на сервере не сработал — недоехавшее везём по SFTP
                    big.AddRange(left);
                }

                var failed = ReceiveSftp(server, settings, sftp, big, options.PreserveTimestamps, received, meter, ref bytes, ct);

                meter.Finish();

                foreach (var job in received.OrderBy(j => j.Entry.Relative, StringComparer.Ordinal))
                {
                    report("получен: " + job.Entry.Relative);
                }

                foreach (var (job, reason) in failed.OrderBy(f => f.Job.Entry.Relative, StringComparer.Ordinal))
                {
                    report($"НЕ СКАЧАН: {job.Entry.Relative} — {reason}");
                }

                var mismatches = verify && !received.IsEmpty
                    ? Verify(ssh, received.ToList(), report, ct)
                    : 0;

                ssh.Disconnect();
                sftp.Disconnect();

                return new DownloadStats(
                    received.Count,
                    skipped,
                    upToDate,
                    dirs,
                    bytes,
                    excluded,
                    links,
                    mismatches,
                    failed.Count,
                    received.Select(j => j.Entry.Relative).ToList());
            },
            ct);
    }

    /// <summary>
    /// Локальный путь: относительный путь с именем скачиваемого объекта — под корнем.
    /// Имя с сервера может быть любым: «..», «\», «:» — всё это здесь безобидно заменяется,
    /// и путь никак не выходит за корень.
    /// </summary>
    private static string LocalPath(string root, string relative)
    {
        var top = Path.GetFullPath(root);
        var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(SafeName);
        var path = Path.GetFullPath(Path.Combine([top, .. parts]));

        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(top) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"имя с сервера выходит за папку скачивания: {relative}");
        }

        return path;
    }

    private static readonly HashSet<string> Devices = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Имя файла с Linux, пригодное для Windows: запрещённое — на «_».</summary>
    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray();
        var safe = new string(chars);

        // Windows молча срезает точки и пробелы в конце — «..» и «a.» превратились бы в другое имя
        if (safe.EndsWith('.') || safe.EndsWith(' '))
        {
            safe = safe[..^1] + "_";
        }

        if (Devices.Contains(safe.Split('.')[0]))
        {
            safe = "_" + safe;
        }

        return safe;
    }

    /// <summary>
    /// Два разных пути с сервера на одно место здесь (Readme и README, «a:b» и «a_b»):
    /// качается первый, о втором — строка в выводе, молча ничего не перезаписывается.
    /// </summary>
    private static List<Entry> Unique(List<Entry> entries, string localRoot, Action<string> report)
    {
        var seen = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<Entry>();

        foreach (var e in entries)
        {
            var local = LocalPath(localRoot, e.Relative);

            if (seen.TryGetValue(local, out var first))
            {
                if (first.Remote != e.Remote && !(first.IsDirectory && e.IsDirectory))
                {
                    report($"не скачан: {e.Remote} — здесь то же имя, что у {first.Remote}");
                }

                continue;
            }

            seen[local] = e;
            kept.Add(e);
        }

        return kept;
    }

    /// <summary>
    /// Опись: тип, размер, дата и путь всего, что под <paramref name="path"/>, одной командой find.
    /// Без find -printf (урезанный busybox) — обходом по SFTP: медленнее, но везде.
    /// </summary>
    private static List<Entry> List(SshClient ssh, Renci.SshNet.SftpClient sftp, string path, Action<string> report, ref int links, CancellationToken ct)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        var (output, error) = SshService.RunInput(ssh, $"LC_ALL=C find -H {SshService.ShellQuote(path)} -printf '%y\\t%s\\t%T@\\t%p\\0'\n", ct);

        if (output.Length == 0)
        {
            if (error.Contains("No such file", StringComparison.OrdinalIgnoreCase))
            {
                report("нет на сервере: " + path);
                return [];
            }

            if (error.Length > 0)
            {
                return ListSftp(sftp, path, name, ref links, ct);
            }
        }

        var list = new List<Entry>();

        foreach (var record in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = record.Split('\t', 4);

            if (parts.Length < 4)
            {
                continue;
            }

            var full = parts[3];

            if (!full.StartsWith(path, StringComparison.Ordinal))
            {
                continue;
            }

            if (parts[0] == "l")
            {
                links++;
                continue;
            }

            if (parts[0] is not ("d" or "f"))
            {
                continue;
            }

            list.Add(new Entry(
                full,
                name + full[path.Length..],
                parts[0] == "d",
                long.TryParse(parts[1], out var size) ? size : 0,
                double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var time) ? time : 0));
        }

        return list;
    }

    private static List<Entry> ListSftp(Renci.SshNet.SftpClient sftp, string path, string name, ref int links, CancellationToken ct)
    {
        var list = new List<Entry>();
        var top = sftp.Get(path);

        if (!top.IsDirectory)
        {
            list.Add(new Entry(path, name, false, top.Length, Unix(top.LastWriteTimeUtc)));
            return list;
        }

        var pending = new Stack<(string Remote, string Relative)>();
        pending.Push((path, name));
        list.Add(new Entry(path, name, true, 0, Unix(top.LastWriteTimeUtc)));

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (remote, relative) = pending.Pop();

            foreach (var item in sftp.ListDirectory(remote))
            {
                if (item.Name is "." or "..")
                {
                    continue;
                }

                if (item.IsSymbolicLink)
                {
                    links++;
                    continue;
                }

                var rel = relative + "/" + item.Name;
                list.Add(new Entry(item.FullName, rel, item.IsDirectory, item.Length, Unix(item.LastWriteTimeUtc)));

                if (item.IsDirectory)
                {
                    pending.Push((item.FullName, rel));
                }
            }
        }

        return list;
    }

    private static double Unix(DateTime utc) => (utc - DateTime.UnixEpoch).TotalSeconds;

    /// <summary>Маски: исключённая папка уходит вместе со всем содержимым.</summary>
    private static IEnumerable<Entry> Filter(List<Entry> entries, ExcludeRules exclude, ref int excluded)
    {
        if (exclude.IsEmpty)
        {
            return entries;
        }

        var cut = new List<string>();
        var kept = new List<Entry>();

        foreach (var e in entries.OrderBy(e => e.Relative, StringComparer.Ordinal))
        {
            if (cut.Any(c => e.Relative.StartsWith(c, StringComparison.Ordinal)))
            {
                continue;
            }

            if (exclude.Matches(e.Relative, e.IsDirectory))
            {
                excluded++;

                if (e.IsDirectory)
                {
                    cut.Add(e.Relative + "/");
                }

                continue;
            }

            kept.Add(e);
        }

        return kept;
    }

    /// <summary>
    /// Что качать — по тем же четырём правилам, что при копировании. Размер сверяется всегда:
    /// недокачанный файл заменяется, что бы ни говорила его дата.
    /// </summary>
    private static (List<Job> Jobs, int Skipped, int UpToDate) Decide(
        SshClient ssh,
        List<Entry> files,
        string localRoot,
        UploadOptions options,
        Action<string> report,
        CancellationToken ct)
    {
        var jobs = new List<Job>();
        var hashCandidates = new List<Job>();
        var skipped = 0;
        var upToDate = 0;

        foreach (var entry in files)
        {
            var local = LocalPath(localRoot, entry.Relative);
            var info = new FileInfo(local);

            if (!info.Exists)
            {
                jobs.Add(new Job(entry, local));
                continue;
            }

            if (!options.Overwrite)
            {
                skipped++;
                continue;
            }

            if (!options.Matching || info.Length != entry.Size)
            {
                jobs.Add(new Job(entry, local));
                continue;
            }

            if (options.MatchByHash)
            {
                hashCandidates.Add(new Job(entry, local));
                continue;
            }

            if (Math.Abs(Unix(info.LastWriteTimeUtc) - entry.MTime) > DateSlackSeconds)
            {
                jobs.Add(new Job(entry, local));
            }
            else
            {
                upToDate++;
            }
        }

        if (hashCandidates.Count > 0)
        {
            report($"сверка сумм до скачивания: файлов того же размера {hashCandidates.Count}");

            var remote = SshService.RemoteSums(ssh, hashCandidates.Select(j => j.Entry.Remote).ToList(), ct, out var complaint);

            if (remote.Count == 0 && complaint.Length > 0)
            {
                report("sha256sum на сервере не ответил: " + complaint + " — такие файлы скачиваются целиком");
            }

            var local = new ConcurrentDictionary<string, string>();

            Parallel.ForEach(
                hashCandidates,
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct },
                j => local[j.Local] = SshService.ComputeLocalSha256(j.Local));

            foreach (var job in hashCandidates)
            {
                if (remote.TryGetValue(job.Entry.Remote, out var sum) && local.TryGetValue(job.Local, out var mine) && sum == mine)
                {
                    upToDate++;
                }
                else
                {
                    jobs.Add(job);
                }
            }
        }

        return (jobs, skipped, upToDate);
    }

    private static IEnumerable<List<Job>> Batches(List<Job> small)
    {
        var batch = new List<Job>();
        long size = 0;

        foreach (var job in small)
        {
            if (batch.Count > 0 && (batch.Count >= BatchFiles || size + job.Entry.Size > BatchBytes))
            {
                yield return batch;
                batch = [];
                size = 0;
            }

            batch.Add(job);
            size += job.Entry.Size;
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }

    /// <summary>
    /// Пачка мелочи одним потоком: на сервере tar (через gzip, если он есть) по списку путей,
    /// здесь — распаковка налету. Вернёт то, что не доехало: это повезут по SFTP.
    /// </summary>
    private static List<Job> ReceiveTar(
        SshClient ssh, List<Job> batch, bool keepDates, ConcurrentBag<Job> received, TransferMeter meter, ref long bytes, CancellationToken ct)
    {
        // в tar пути без ведущей «/» — от корня, так имена в архиве однозначны
        var byName = batch.ToDictionary(j => j.Entry.Remote.TrimStart('/'), StringComparer.Ordinal);
        var left = new HashSet<Job>(batch);

        try
        {
            using var cmd = ssh.CreateCommand(
                "if command -v gzip >/dev/null 2>&1; then tar -C / -cf - --null -T - | gzip -1; else tar -C / -cf - --null -T -; fi");

            var run = cmd.BeginExecute();

            using (var input = new BufferedStream(cmd.CreateInputStream(), 64 * 1024))
            {
                foreach (var name in byName.Keys)
                {
                    input.Write(Encoding.UTF8.GetBytes(name));
                    input.WriteByte(0);
                }
            }

            using (var registration = ct.Register(() => TryCancel(cmd)))
            {
                var stream = new BufferedStream(cmd.OutputStream, 256 * 1024);

                // gzip узнаём по сигнатуре: на сервере его могло и не быть
                var head = new byte[2];
                var got = stream.Read(head, 0, 2);
                Stream source = new PrefixStream(head, got, stream);

                if (got == 2 && head[0] == 0x1F && head[1] == 0x8B)
                {
                    source = new GZipStream(source, CompressionMode.Decompress);
                }

                using var reader = new TarReader(source);

                while (reader.GetNextEntry() is { } entry)
                {
                    ct.ThrowIfCancellationRequested();

                    if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                        || !byName.TryGetValue(entry.Name, out var job)
                        || entry.DataStream is null)
                    {
                        continue;
                    }

                    var temp = TempName(job.Local);

                    try
                    {
                        using (var file = meter.Wrap(File.Create(temp)))
                        {
                            entry.DataStream.CopyTo(file);
                        }

                        Place(temp, job, keepDates);
                    }
                    catch (OperationCanceledException)
                    {
                        TryDelete(temp);
                        throw;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // здесь файл не записать (занят, нет прав) — попробует SFTP и скажет причину
                        TryDelete(temp);
                        continue;
                    }

                    Interlocked.Add(ref bytes, job.Entry.Size);
                    received.Add(job);
                    left.Remove(job);
                }
            }

            cmd.EndExecute(run);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // пачка не дошла целиком — недоехавшее довезёт SFTP
        }

        return left.ToList();
    }

    private static void TryCancel(SshCommand cmd)
    {
        try
        {
            cmd.CancelAsync();
        }
        catch
        {
            // уже закончилась
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // не удалился — останется временный с точкой, следующий раз перезапишется
        }
    }

    /// <summary>
    /// Крупное и то, что не доехало пачкой, — по SFTP, в несколько подключений сразу.
    /// Не скачался один файл (пропал, нет прав) — остальные едут; вернёт нескачанное с причиной.
    /// </summary>
    private static List<(Job Job, string Reason)> ReceiveSftp(
        ServerEntry server,
        AppSettings settings,
        Renci.SshNet.SftpClient first,
        List<Job> jobs,
        bool keepDates,
        ConcurrentBag<Job> received,
        TransferMeter meter,
        ref long bytes,
        CancellationToken ct)
    {
        var failed = new ConcurrentBag<(Job Job, string Reason)>();

        if (jobs.Count == 0)
        {
            return [];
        }

        var pool = new ConcurrentBag<Renci.SshNet.SftpClient> { first };
        var own = new List<Renci.SshNet.SftpClient>();

        for (var i = 1; i < Math.Min(BigFileConnections, jobs.Count); i++)
        {
            Renci.SshNet.SftpClient? extra = null;

            try
            {
                extra = SshService.NewSftp(server, settings);
                extra.Connect();
                own.Add(extra);
                pool.Add(extra);
            }
            catch
            {
                // лишнее подключение не далось — едем на тех, что есть
                extra?.Dispose();
                break;
            }
        }

        long total = 0;

        try
        {
            Parallel.ForEach(
                jobs.OrderByDescending(j => j.Entry.Size),
                new ParallelOptions { MaxDegreeOfParallelism = pool.Count, CancellationToken = ct },
                job =>
                {
                    if (!pool.TryTake(out var client))
                    {
                        client = first;
                    }

                    var temp = TempName(job.Local);

                    try
                    {
                        using (var file = meter.Wrap(File.Create(temp)))
                        {
                            client.DownloadFile(job.Entry.Remote, file);
                        }

                        Place(temp, job, keepDates);
                        Interlocked.Add(ref total, job.Entry.Size);
                        received.Add(job);
                    }
                    catch (Exception ex)
                    {
                        TryDelete(temp);

                        if (ex is OperationCanceledException || ct.IsCancellationRequested)
                        {
                            throw new OperationCanceledException(ct);
                        }

                        failed.Add((job, ex.Message));
                    }
                    finally
                    {
                        pool.Add(client);
                    }
                });
        }
        finally
        {
            foreach (var client in own)
            {
                client.Dispose();
            }
        }

        Interlocked.Add(ref bytes, total);
        return failed.ToList();
    }

    /// <summary>Временный файл рядом с целевым: точка спереди, чтобы не мозолил глаза.</summary>
    private static string TempName(string local)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        return Path.Combine(Path.GetDirectoryName(local)!, "." + Path.GetFileName(local) + ".mss-part");
    }

    /// <summary>Скачанное встаёт на место целиком; дата — как на сервере.</summary>
    private static void Place(string temp, Job job, bool keepDates)
    {
        if (keepDates && job.Entry.MTime > 0)
        {
            File.SetLastWriteTimeUtc(temp, DateTime.UnixEpoch.AddSeconds(job.Entry.MTime));
        }

        File.Move(temp, job.Local, overwrite: true);
    }

    /// <summary>Сверка скачанного: локальный SHA-256 против sha256sum на сервере.</summary>
    private static int Verify(SshClient ssh, List<Job> jobs, Action<string> report, CancellationToken ct)
    {
        report($"сверка сумм после скачивания: файлов {jobs.Count}");

        var remote = SshService.RemoteSums(ssh, jobs.Select(j => j.Entry.Remote).ToList(), ct, out var complaint);
        var bad = 0;

        foreach (var job in jobs)
        {
            ct.ThrowIfCancellationRequested();

            var mine = SshService.ComputeLocalSha256(job.Local);

            if (!remote.TryGetValue(job.Entry.Remote, out var sum))
            {
                bad++;
                report($"нет суммы с сервера: {job.Entry.Relative}" + (complaint.Length > 0 ? $" ({complaint})" : string.Empty));
            }
            else if (sum != mine)
            {
                bad++;
                report($"СУММА НЕ СОВПАЛА: {job.Entry.Relative} — сервер {sum}, здесь {mine}");
            }
        }

        report(bad == 0 ? "суммы совпали у всех скачанных файлов" : $"расхождений сумм: {bad}");
        return bad;
    }

    /// <summary>Поток, который сначала отдаёт уже прочитанные байты, потом остальное.</summary>
    private sealed class PrefixStream(byte[] prefix, int count, Stream rest) : Stream
    {
        private int _offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int length)
        {
            if (_offset < count)
            {
                var n = Math.Min(length, count - _offset);
                Array.Copy(prefix, _offset, buffer, offset, n);
                _offset += n;
                return n;
            }

            return rest.Read(buffer, offset, length);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int length) => throw new NotSupportedException();
    }
}
