using System.Collections.Concurrent;
using System.IO;
using MultiServerSync.Core;
using MultiServerSync.Models;

namespace MultiServerSync.Services;

/// <summary>
/// Копирование и сравнение внутри этого компьютера — «Проводник», когда справа открыт «Этот компьютер».
/// Правила те же, что у заливки на сервер: исключения, «которых нет / с другой датой / с другой суммой»,
/// вопрос перед заменой, дата изменения, сверка сумм после. Прав и владельца нет.
/// Отчёт — теми же <see cref="UploadStats"/> и <see cref="CompareStats"/>: строка, вывод и итог как у сервера.
/// </summary>
public static class LocalCopyService
{
    /// <summary>Запас при сравнении дат — тот же, что у сервера.</summary>
    private const double DateTolerance = 2;

    /// <summary>Сколько файлов копируется сразу: диск не сеть, больше — только толкотня головок.</summary>
    private const int Workers = 4;

    private sealed record FileJob(string Source, string Target);

    private sealed record DirJob(string Source, string Target);

    private sealed class Plan
    {
        public List<FileJob> Files { get; } = [];

        public List<DirJob> Dirs { get; } = [];

        public HashSet<string> Roots { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public static Task<UploadStats> CopyAsync(
        ServerEntry who,
        IReadOnlyList<TransferItem> items,
        string targetRoot,
        UploadOptions options,
        ConcurrentDictionary<string, string> hashes,
        Action<string> report,
        CancellationToken ct,
        Action<UploadStep>? step = null,
        Action<long, long>? progress = null,
        ConfirmReplace? confirmReplace = null) =>
        Task.Run(() => Copy(who, items, targetRoot, options, hashes, report, ct, step, progress, confirmReplace), ct);

    private static UploadStats Copy(
        ServerEntry who,
        IReadOnlyList<TransferItem> items,
        string targetRoot,
        UploadOptions options,
        ConcurrentDictionary<string, string> hashes,
        Action<string> report,
        CancellationToken ct,
        Action<UploadStep>? step,
        Action<long, long>? progress,
        ConfirmReplace? confirmReplace)
    {
        var clock = new StageClock();
        var meter = new TransferMeter(progress, ct);
        var root = Path.GetFullPath(targetRoot);

        CheckTarget(items, root);

        var exclude = options.Excludes();
        var plan = BuildPlan(items, root, exclude);

        if (exclude.Summary() is { } excluded)
        {
            report(excluded);
        }

        step?.Invoke(options.MatchByHash && options.Overwrite ? UploadStep.CheckingHashes : UploadStep.CheckingDates);

        // содержимое читается с обеих сторон, на сотнях гигабайт это долго — полоса по прочитанному
        var hashMeter = ContentMeter(plan, options, progress, ct);

        var skipped = 0;
        var upToDate = 0;
        var redated = 0;
        var send = new ConcurrentBag<(FileJob Job, bool Replaces)>();

        // сначала решаем по каждому файлу, потом копируем: если замену не подтвердят, в назначении ничего нового
        ForFiles(plan.Files, ct, job =>
        {
            var source = new FileInfo(job.Source);

            if (Directory.Exists(job.Target))
            {
                report($"не скопирован, в назначении на этом месте папка: {job.Target}");
                Interlocked.Increment(ref skipped);
                return;
            }

            var target = new FileInfo(job.Target);

            if (!target.Exists)
            {
                send.Add((job, false));
                return;
            }

            if (!options.Overwrite)
            {
                Interlocked.Increment(ref skipped);
                return;
            }

            if (options.Matching)
            {
                var reason = Difference(source, target, options, hashes, hashMeter, ct);

                if (reason is null)
                {
                    Interlocked.Increment(ref upToDate);

                    // содержимое то же, а дата чужая — переносим дату, файл не копируем
                    if (options.PreserveTimestamps && Math.Abs((source.LastWriteTimeUtc - target.LastWriteTimeUtc).TotalSeconds) > DateTolerance)
                    {
                        Writable(target);
                        target.LastWriteTimeUtc = source.LastWriteTimeUtc;
                        Interlocked.Increment(ref redated);
                    }

                    return;
                }

                report($"замена ({reason}): {job.Target}");
            }

            send.Add((job, true));
        });

        clock.Mark(hashMeter is null ? "разбор" : "суммы до");

        // полоса сумм кончилась — копированию своя, с нуля
        progress?.Invoke(0, 0);

        var jobs = send.ToList();
        var replacing = jobs.Where(j => j.Replaces).Select(j => j.Job.Target).Order(StringComparer.OrdinalIgnoreCase).ToList();

        if (confirmReplace is not null && replacing.Count > 0)
        {
            switch (confirmReplace(who, replacing))
            {
                case ReplaceChoice.Cancel:
                    report("копирование отменено: замену файлов не подтвердили");
                    throw new OperationCanceledException();

                case ReplaceChoice.Skip:
                    jobs.RemoveAll(j => j.Replaces);
                    skipped += replacing.Count;
                    report($"не заменены, оставлены как есть: {replacing.Count}");
                    break;
            }
        }

        // папки — заранее и по порядку, родитель раньше детей
        var createdDirs = new List<string>();

        if (!Directory.Exists(root))
        {
            Directory.CreateDirectory(root);
            createdDirs.Add(root);
        }

        foreach (var dir in plan.Dirs)
        {
            ct.ThrowIfCancellationRequested();

            if (File.Exists(dir.Target))
            {
                throw new IOException($"В назначении на месте папки лежит файл: {dir.Target}");
            }

            if (!Directory.Exists(dir.Target))
            {
                Directory.CreateDirectory(dir.Target);
                createdDirs.Add(dir.Target);
            }
        }

        clock.Mark("папки");

        var copied = new ConcurrentBag<UploadedFile>();
        var bytes = 0L;

        if (jobs.Count > 0)
        {
            step?.Invoke(UploadStep.Copying);

            var ordered = jobs.Select(j => j.Job).OrderBy(j => j.Target, StringComparer.OrdinalIgnoreCase).ToList();
            meter.Total = ordered.Sum(j => new FileInfo(j.Source).Length);
            progress?.Invoke(0, meter.Total); // 0 из N — полоса появляется сразу

            RunParallel(ordered, new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = ct }, job =>
            {
                var size = CopyFile(job.Source, job.Target, options.PreserveTimestamps, meter);
                Interlocked.Add(ref bytes, size);
                copied.Add(new UploadedFile(job.Source, job.Target, size));
            });

            meter.Finish();
            clock.Mark("копирование");
        }

        // время папок — последним: запись файлов внутрь его сбивает
        if (options.PreserveTimestamps)
        {
            foreach (var dir in plan.Dirs)
            {
                var stamp = Directory.GetLastWriteTimeUtc(dir.Source);

                if (Math.Abs((Directory.GetLastWriteTimeUtc(dir.Target) - stamp).TotalSeconds) > DateTolerance)
                {
                    TryRun(() => Directory.SetLastWriteTimeUtc(dir.Target, stamp));
                }
            }

            clock.Mark("даты");
        }

        return new UploadStats(
            copied.Count,
            skipped,
            upToDate,
            redated,
            createdDirs.Count,
            bytes,
            copied.OrderBy(f => f.RemotePath, StringComparer.OrdinalIgnoreCase).ToList(),
            createdDirs,
            clock);
    }

    /// <summary>
    /// Сверка после копирования: оригинал против только что записанного, побайтно — с диска, не из кэша.
    /// Вернёт число расхождений.
    /// </summary>
    public static Task<int> VerifyAsync(
        IReadOnlyList<UploadedFile> files,
        ConcurrentDictionary<string, string> hashes,
        Action<string> report,
        CancellationToken ct,
        Action<long, long>? progress = null) =>
        Task.Run(() =>
        {
            var mismatches = 0;
            var meter = new TransferMeter(progress, ct) { Total = files.Sum(f => f.Size) * 2 };
            progress?.Invoke(0, meter.Total);

            ForFiles(files.Select(f => new FileJob(f.LocalPath, f.RemotePath)).ToList(), ct, file =>
            {
                if (!SameContent(file.Source, file.Target, meter, ct))
                {
                    Interlocked.Increment(ref mismatches);
                    report($"РАСХОЖДЕНИЕ: {file.Target} — содержимое не совпало с оригиналом {file.Source}");
                }
            });

            meter.Finish();
            return mismatches;
        }, ct);

    /// <summary>
    /// Сравнение вместо копирования: тот же план и то же правило, что у копирования, ничего не меняется.
    /// Сверх сравниваемого — что ещё лежит в папках назначения, только списком.
    /// </summary>
    public static Task<CompareStats> CompareAsync(
        IReadOnlyList<TransferItem> items,
        string targetRoot,
        UploadOptions options,
        ConcurrentDictionary<string, string> hashes,
        Action<string> report,
        CancellationToken ct,
        Action<long, long>? progress = null) =>
        Task.Run(() =>
        {
            var clock = new StageClock();
            var root = Path.GetFullPath(targetRoot);
            var exclude = options.Excludes();
            var plan = BuildPlan(items, root, exclude);

            if (exclude.Summary() is { } excluded)
            {
                report(excluded);
            }

            report(CompareRule(options));

            var hashMeter = ContentMeter(plan, options, progress, ct);

            if (hashMeter is not null)
            {
                report($"файлы одинакового размера сверяются побайтно, до первого отличия: до {TransferItem.Format(hashMeter.Total / 2)} с каждой стороны");
            }

            var missing = new ConcurrentBag<string>();
            var different = new ConcurrentBag<CompareDiff>();
            var ignored = new ConcurrentBag<CompareDiff>();
            var same = 0;

            ForFiles(plan.Files, ct, job =>
            {
                if (Directory.Exists(job.Target))
                {
                    different.Add(new CompareDiff(job.Target, "в назначении на этом месте папка"));
                    return;
                }

                var target = new FileInfo(job.Target);

                if (!target.Exists)
                {
                    missing.Add(job.Target);
                    return;
                }

                var source = new FileInfo(job.Source);

                // существующие файлы этот режим не трогает: их отличия на итог не влияют, только в вывод
                if (!options.Overwrite)
                {
                    Interlocked.Increment(ref same);

                    if ((source.Length != target.Length ? SizeDifference(source.Length, target.Length) : DateDifference(source, target)) is { } note)
                    {
                        ignored.Add(new CompareDiff(job.Target, note));
                    }

                    return;
                }

                var reason = options.Matching
                    ? Difference(source, target, options, hashes, hashMeter, ct)
                    : source.Length != target.Length ? SizeDifference(source.Length, target.Length) : null;

                if (reason is not null)
                {
                    different.Add(new CompareDiff(job.Target, reason));
                    return;
                }

                Interlocked.Increment(ref same);

                // сверка по сумме и по размеру на дату не смотрит: расхождение — только в вывод
                if (DateDifference(source, target) is { } dateNote)
                {
                    ignored.Add(new CompareDiff(job.Target, dateNote));
                }
            });

            // папки — только верхние из отсутствующих: внутри них и так ничего нет
            var absentDirs = new List<string>();

            foreach (var dir in plan.Dirs)
            {
                ct.ThrowIfCancellationRequested();

                if (absentDirs.Any(a => dir.Target.StartsWith(a + "\\", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (!Directory.Exists(dir.Target))
                {
                    absentDirs.Add(dir.Target);
                }
            }

            hashMeter?.Finish();
            clock.Mark("сверка");

            return new CompareStats(
                same,
                missing.Order(StringComparer.OrdinalIgnoreCase).ToList(),
                different.OrderBy(d => d.Path, StringComparer.OrdinalIgnoreCase).ToList(),
                ignored.OrderBy(d => d.Path, StringComparer.OrdinalIgnoreCase).ToList(),
                absentDirs,
                clock,
                Extras(plan, items, root, absentDirs));
        }, ct);

    /// <summary>Два файла компьютера одного размера — побайтно, до первого отличия, с полосой по прочитанному.</summary>
    public static Task<bool> SameFilesAsync(string a, string b, Action<long, long>? progress, CancellationToken ct) =>
        Task.Run(() =>
        {
            var meter = new TransferMeter(progress, ct) { Total = new FileInfo(a).Length * 2 };
            progress?.Invoke(0, meter.Total);

            var same = SameContent(a, b, meter, ct);
            meter.Finish();
            return same;
        }, ct);

    /// <summary>Что ещё лежит в папках назначения сверх сравниваемого. Лишняя папка — одной строкой со счётом файлов.</summary>
    private static List<CompareExtra> Extras(Plan plan, IReadOnlyList<TransferItem> items, string root, List<string> absentDirs)
    {
        var compared = new HashSet<string>(plan.Files.Select(f => f.Target).Concat(plan.Dirs.Select(d => d.Target)), StringComparer.OrdinalIgnoreCase);
        var extras = new List<CompareExtra>();

        foreach (var item in items.Where(i => i.IsDirectory))
        {
            var top = Path.Combine(root, item.Name);

            if (!plan.Roots.Contains(top) || absentDirs.Contains(top, StringComparer.OrdinalIgnoreCase) || !Directory.Exists(top))
            {
                continue;
            }

            Walk(top);
        }

        return extras;

        void Walk(string dir)
        {
            foreach (var file in SafeEnumerate(() => Directory.EnumerateFiles(dir)))
            {
                if (!compared.Contains(file))
                {
                    extras.Add(new CompareExtra(file, false, 0));
                }
            }

            foreach (var sub in SafeEnumerate(() => Directory.EnumerateDirectories(dir)))
            {
                if (compared.Contains(sub))
                {
                    Walk(sub);
                }
                else
                {
                    extras.Add(new CompareExtra(sub, true, SafeEnumerate(() => Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories)).Count()));
                }
            }
        }
    }

    private static IEnumerable<string> SafeEnumerate(Func<IEnumerable<string>> list)
    {
        try
        {
            return list().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Папку нельзя копировать в неё саму или внутрь себя, файл — на самого себя.</summary>
    private static void CheckTarget(IReadOnlyList<TransferItem> items, string root)
    {
        foreach (var item in items)
        {
            var source = Path.GetFullPath(item.FullPath).TrimEnd('\\');
            var target = Path.Combine(root, item.Name);

            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Папка назначения та же, где лежит «{item.Name}»: копировать некуда.");
            }

            if (item.IsDirectory && (root + "\\").StartsWith(source + "\\", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Папку «{item.Name}» нельзя копировать внутрь неё самой.");
            }
        }
    }

    /// <summary>Что копировать: все файлы и папки выбранного, с путями в назначении. Папка едет вместе со своим именем.</summary>
    private static Plan BuildPlan(IReadOnlyList<TransferItem> items, string root, ExcludeFilter exclude)
    {
        var plan = new Plan();

        foreach (var item in items)
        {
            if (exclude.Excludes(item.Name, item.IsDirectory))
            {
                continue;
            }

            var target = Path.Combine(root, item.Name);
            plan.Roots.Add(target);

            if (!item.IsDirectory)
            {
                plan.Files.Add(new FileJob(item.FullPath, target));
            }
            else if (!AddDirectory(plan, item.FullPath, target, item.Name, exclude))
            {
                // папка целиком под исключением, в неё заходили ради «Включить» и ничего не нашли
                plan.Roots.Remove(target);
            }
        }

        return plan;
    }

    /// <param name="relative">Путь от копируемого объекта включительно — по нему сверяются маски.</param>
    /// <returns>Папка едет: сама или ради того, что в ней включено.</returns>
    private static bool AddDirectory(Plan plan, string sourceDir, string targetDir, string relative, ExcludeFilter exclude)
    {
        var onlyForIncludes = exclude.OnlyForIncludes(relative);
        var at = plan.Dirs.Count;
        var filesBefore = plan.Files.Count;

        if (!onlyForIncludes)
        {
            plan.Dirs.Add(new DirJob(sourceDir, targetDir));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var name = Path.GetFileName(file);

            if (!exclude.Excludes(relative + "/" + name, isDirectory: false))
            {
                plan.Files.Add(new FileJob(file, Path.Combine(targetDir, name)));
            }
        }

        foreach (var dir in Directory.EnumerateDirectories(sourceDir))
        {
            var name = Path.GetFileName(dir);

            // исключённая папка не обходится вовсе
            if (!exclude.Excludes(relative + "/" + name, isDirectory: true))
            {
                AddDirectory(plan, dir, Path.Combine(targetDir, name), relative + "/" + name, exclude);
            }
        }

        if (!onlyForIncludes)
        {
            return true;
        }

        if (plan.Files.Count == filesBefore)
        {
            return false;
        }

        // родитель раньше детей: папки создаются по этому порядку
        plan.Dirs.Insert(at, new DirJob(sourceDir, targetDir));
        return true;
    }

    /// <summary>
    /// Чем файл-оригинал отличается от файла в назначении, или null, если копировать нечего.
    /// Размер сверяется всегда; дальше — содержимое или дата, как выбрано в параметрах.
    /// «С другой суммой» на одном компьютере — побайтно: ответ тот же, а разные файлы отсеиваются
    /// на первом отличии, без чтения до конца.
    /// </summary>
    private static string? Difference(FileInfo source, FileInfo target, UploadOptions options, ConcurrentDictionary<string, string> hashes, TransferMeter? meter, CancellationToken ct)
    {
        if (source.Length != target.Length)
        {
            return SizeDifference(source.Length, target.Length);
        }

        if (options.MatchByHash)
        {
            return SameContent(source.FullName, target.FullName, meter, ct) ? null : "другое содержимое";
        }

        return DateDifference(source, target);
    }

    /// <summary>По какому правилу сравниваются файлы — первой строкой в выводе сравнения.</summary>
    private static string CompareRule(UploadOptions options) =>
        !options.Overwrite ? SshService.CompareRule(options)
        : options.MatchByHash ? "сравнение по размеру и содержимому побайтно, дата на итог не влияет"
        : SshService.CompareRule(options);

    /// <summary>Файл не меньше этого сверяется один, без соседей: на одном диске параллельное чтение больших файлов только мешает.</summary>
    private const long BigFile = 64L * 1024 * 1024;

    /// <summary>Мелкие файлы — несколько сразу (время уходит на открытие), крупные — по одному подряд.</summary>
    private static void ForFiles(IReadOnlyList<FileJob> files, CancellationToken ct, Action<FileJob> body)
    {
        var big = new List<FileJob>();
        var small = new List<FileJob>();

        foreach (var file in files)
        {
            (Size(file.Source) >= BigFile ? big : small).Add(file);
        }

        RunParallel(small, new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = ct }, body);

        foreach (var file in big)
        {
            ct.ThrowIfCancellationRequested();
            body(file);
        }

        static long Size(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return 0;
            }
        }
    }

    /// <summary>
    /// Параллельный обход: сбой или отмена выходит наружу сам по себе, а не обёрткой
    /// «произошла одна или несколько ошибок» — строке нужна настоящая причина, а «Остановить» — отмена.
    /// </summary>
    private static void RunParallel<T>(IEnumerable<T> source, ParallelOptions options, Action<T> body)
    {
        try
        {
            Parallel.ForEach(source, options, body);
        }
        catch (AggregateException ae) when (ae.InnerExceptions.Count > 0)
        {
            var first = ae.InnerExceptions.FirstOrDefault(e => e is OperationCanceledException) ?? ae.InnerExceptions[0];
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
        }
    }

    /// <summary>Сколько читается за раз с каждой стороны.</summary>
    private const int Chunk = 4 << 20;

    /// <summary>
    /// Одинаковое ли содержимое — побайтно, кусками с обеих сторон, до первого отличия. Непрочитанный
    /// остаток засчитывается полосе сразу. Отмена обрывает чтение на ближайшем куске.
    /// </summary>
    private static bool SameContent(string a, string b, TransferMeter? meter, CancellationToken ct)
    {
        using var fa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        using var fb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);

        var total = Math.Max(fa.Length, fb.Length);
        var ba = System.Buffers.ArrayPool<byte>.Shared.Rent(Chunk);
        var bb = System.Buffers.ArrayPool<byte>.Shared.Rent(Chunk);
        var done = 0L;

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                // оба файла читаются одновременно: на разных дисках сверка идёт почти вдвое быстрее
                var readB = Task.Run(() => fb.ReadAtLeast(bb.AsSpan(0, Chunk), Chunk, throwOnEndOfStream: false));
                int na;

                try
                {
                    na = fa.ReadAtLeast(ba.AsSpan(0, Chunk), Chunk, throwOnEndOfStream: false);
                }
                finally
                {
                    // второй файл дочитывает свой кусок в тот же буфер — ждём его, даже если первый упал
                    ((IAsyncResult)readB).AsyncWaitHandle.WaitOne();
                }

                var nb = readB.GetAwaiter().GetResult();

                if (na == 0 && nb == 0)
                {
                    return true;
                }

                done += Math.Max(na, nb);
                meter?.Add((long)Math.Max(na, nb) * 2);

                if (na != nb || !ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, nb)))
                {
                    meter?.Add(Math.Max(0, total - done) * 2);
                    return false;
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(ba);
            System.Buffers.ArrayPool<byte>.Shared.Return(bb);
        }
    }

    /// <summary>
    /// Полоса для сверки содержимого: сколько байт придётся прочитать — пары одинакового размера, обе стороны.
    /// null — сверять содержимое не нужно (не тот режим или сравнивать нечего).
    /// </summary>
    private static TransferMeter? ContentMeter(Plan plan, UploadOptions options, Action<long, long>? progress, CancellationToken ct)
    {
        if (!options.Overwrite || !options.MatchByHash || progress is null)
        {
            return null;
        }

        var total = 0L;

        foreach (var job in plan.Files)
        {
            ct.ThrowIfCancellationRequested();

            var target = new FileInfo(job.Target);

            if (target.Exists && new FileInfo(job.Source) is { Exists: true } source && source.Length == target.Length)
            {
                total += source.Length * 2;
            }
        }

        if (total == 0)
        {
            return null;
        }

        progress(0, total);
        return new TransferMeter(progress, ct) { Total = total };
    }

    private static string SizeDifference(long source, long target) =>
        $"другой размер · откуда {TransferItem.Format(source)}, куда {TransferItem.Format(target)}";

    private static string? DateDifference(FileInfo source, FileInfo target)
    {
        var drift = (source.LastWriteTimeUtc - target.LastWriteTimeUtc).TotalSeconds;

        if (Math.Abs(drift) <= DateTolerance)
        {
            return null;
        }

        return (drift > 0 ? "откуда свежее" : "куда свежее")
            + $" · откуда {source.LastWriteTime:dd.MM.yyyy HH:mm:ss}"
            + $", куда {target.LastWriteTime:dd.MM.yyyy HH:mm:ss}";
    }

    /// <summary>
    /// Копия файла через временный рядом: оборвалось — прежний файл в назначении цел.
    /// Без «сохранять дату» у копии дата копирования, как у заливки на сервер.
    /// </summary>
    private static long CopyFile(string source, string target, bool keepDate, TransferMeter meter)
    {
        var temp = Path.Combine(Path.GetDirectoryName(target)!, "." + Path.GetFileName(target) + ".part-" + Guid.NewGuid().ToString("N")[..8]);
        long size;

        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
            {
                input.CopyTo(meter.Wrap(output), 1 << 20);
                size = output.Length;
            }

            if (keepDate)
            {
                File.SetLastWriteTimeUtc(temp, File.GetLastWriteTimeUtc(source));
            }

            if (File.Exists(target))
            {
                Writable(new FileInfo(target));
            }

            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            TryRun(() => File.Delete(temp));
            throw;
        }

        return size;
    }

    /// <summary>Файл «только для чтения» в назначении заменить нельзя — снимаем признак, как это сделал бы проводник по «Заменить».</summary>
    private static void Writable(FileInfo file)
    {
        if (file.IsReadOnly)
        {
            file.IsReadOnly = false;
        }
    }

    private static void TryRun(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // дата или уборка временного файла — не повод сорвать копирование
        }
    }
}
