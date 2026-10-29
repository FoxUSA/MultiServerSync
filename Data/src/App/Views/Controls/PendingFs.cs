using MultiServerSync.Services;

namespace MultiServerSync.Views.Controls;

/// <summary>
/// Действия «Проводника», которых сервер ещё не закончил: созданное, переименованное, удалённое,
/// скопированное и перенесённое. Два дела:
/// 1. Показ. Пока ответа нет, любой показ папки — в любой панели, и после ухода и возврата —
///    учитывает сделанное: иначе оно на время пропадало или возвращалось. Строка, которую сервер
///    ещё делает, помечена (<see cref="LabelOf"/>). Действие закончилось — панели с этой папкой
///    тихо перечитывают её на месте.
/// 2. Очередь. Действие с папкой ждёт действий, начатых раньше с ней же, с её содержимым или с
///    папками над ней (<see cref="RunAsync"/>): «создать → войти → создать внутри → удалить» идёт по
///    порядку, а не падает с «такой папки нет». Не прошло первое — зависящие от него отменяются
///    одной понятной записью (<see cref="CancelledException"/>), без россыпи ошибок. Действия с
///    разными папками друг друга не ждут.
/// Всё — в потоке окна.
/// </summary>
public static class PendingFs
{
    private static readonly List<Change> Changes = new();
    private static readonly List<Op> Ops = new();

    /// <summary>Сделанное встало на место или ушло: сторона и папка, которую стоит перечитать.</summary>
    public static event Action<string, string>? Settled;

    /// <summary>Действие началось или кончилось: строкам папки пора обновить пометку «сервер ещё выполняет».</summary>
    public static event Action<string, string>? Marked;

    /// <summary>Сторона: компьютер или конкретный сервер — у двух панелей одного сервера она общая.</summary>
    public static string SideOf(IFileSource source) =>
        source is RemoteSource remote ? "srv:" + remote.Server.Id : "pc";

    // ── показ ───────────────────────────────────────────────────────────────

    /// <summary>В папке вот-вот появится строка.</summary>
    public static IDisposable Add(IFileSource source, string dir, FsEntry entry) =>
        Track(new Change(SideOf(source), source.Normalize(dir), entry, null));

    /// <summary>Строка вот-вот уйдёт из папки.</summary>
    public static IDisposable Remove(IFileSource source, string dir, string path) =>
        Track(new Change(SideOf(source), source.Normalize(dir), null, path));

    /// <summary>Список папки с поправкой на незаконченные действия.</summary>
    public static IReadOnlyList<FsEntry> Apply(IFileSource source, string dir, IReadOnlyList<FsEntry> items)
    {
        var side = SideOf(source);
        var at = source.Normalize(dir);
        var mine = Changes.Where(c => c.Side == side && c.Dir == at).ToList();

        if (mine.Count == 0)
        {
            return items;
        }

        var gone = mine.Where(c => c.Path is not null).Select(c => c.Path!).ToHashSet(StringComparer.Ordinal);
        var result = items.Where(i => !gone.Contains(i.FullPath)).ToList();
        var names = result.Select(i => i.Name).ToHashSet(StringComparer.Ordinal);

        // созданное и тут же переименованное: старое имя уходит, новое встаёт
        foreach (var change in mine)
        {
            if (change.Entry is { } entry && !gone.Contains(entry.FullPath) && names.Add(entry.Name))
            {
                result.Add(entry);
            }
        }

        return result;
    }

    /// <summary>Чем сервер сейчас занят с этим путём (или с папкой над ним); null — ничем.</summary>
    public static string? LabelOf(IFileSource source, string path)
    {
        var side = SideOf(source);
        var at = source.Normalize(path);
        return Ops.FirstOrDefault(o => o.Side == side && o.Paths.Any(p => Same(side, p, at) || IsUnder(side, at, p)))?.Label;
    }

    // ── очередь ─────────────────────────────────────────────────────────────

    /// <summary>Зависящее действие отменено: то, от чего оно зависело, не прошло.</summary>
    public sealed class CancelledException(string label, Exception cause)
        : Exception($"отменено, потому что не прошло {label}", cause)
    {
        /// <summary>Что не прошло — для записи в журнал.</summary>
        public string Label { get; } = label;
    }

    /// <summary>Незаконченные действия с этим путём и с папками над ним — переход ждёт их.</summary>
    public static IReadOnlyList<(string Label, Task Task)> Ahead(IFileSource source, string path)
    {
        var side = SideOf(source);
        var at = source.Normalize(path);
        return Ops
            .Where(o => o.Side == side && o.Paths.Any(p => Same(side, p, at) || IsUnder(side, at, p)))
            .Select(o => (o.Label, o.Task))
            .ToList();
    }

    /// <summary>
    /// Выполнить действие по очереди: сначала ждут действия, начатые раньше с теми же путями, с их
    /// содержимым или с папками над ними. Не прошло одно из них — это отменяется с
    /// <see cref="CancelledException"/>, своя работа не начинается.
    /// </summary>
    /// <param name="paths">Что действие трогает: сами пути и, у копирования, пути назначения.</param>
    /// <param name="label">Что делается, для журнала и подсказок: «создание «123»».</param>
    public static async Task RunAsync(IFileSource source, IEnumerable<string> paths, string label, Func<Task> work)
    {
        var side = SideOf(source);
        var mine = paths.Select(source.Normalize).Distinct().ToArray();
        var ahead = Ops.Where(o => o.Side == side && o.Paths.Any(p => mine.Any(m => Same(side, p, m) || IsUnder(side, m, p) || IsUnder(side, p, m)))).ToList();

        var done = new TaskCompletionSource();
        var op = new Op(side, mine, label, done.Task);
        Ops.Add(op);

        // никто не дождался — исключение не должно всплыть как необработанное
        _ = done.Task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

        var dirs = mine.Select(m => source.Parent(m) is { } parent ? source.Normalize(parent) : m).Distinct().ToList();
        dirs.ForEach(d => Marked?.Invoke(side, d));

        try
        {
            foreach (var earlier in ahead)
            {
                try
                {
                    await earlier.Task;
                }
                catch (Exception ex)
                {
                    throw new CancelledException(earlier.Label, ex);
                }
            }

            await work();
            done.SetResult();
        }
        catch (Exception ex)
        {
            done.SetException(ex);
            throw;
        }
        finally
        {
            Ops.Remove(op);
            dirs.ForEach(d => Marked?.Invoke(side, d));
        }
    }

    // ── внутреннее ──────────────────────────────────────────────────────────

    private static StringComparison Cmp(string side) => side == "pc" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool Same(string side, string a, string b) => string.Equals(a, b, Cmp(side));

    /// <summary>path лежит внутри dir (на любой глубине).</summary>
    private static bool IsUnder(string side, string path, string dir)
    {
        if (path.Length <= dir.Length || !path.StartsWith(dir, Cmp(side)))
        {
            return false;
        }

        // граница имени: «/a/b» внутри «/a», но «/a/bc» — нет; корень «/» и «D:\» уже кончаются разделителем
        return dir.EndsWith('/') || dir.EndsWith('\\') || path[dir.Length] is '/' or '\\';
    }

    private static IDisposable Track(Change change)
    {
        Changes.Add(change);
        return new Token(change);
    }

    private sealed record Change(string Side, string Dir, FsEntry? Entry, string? Path);

    private sealed record Op(string Side, string[] Paths, string Label, Task Task);

    private sealed class Token(Change change) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done)
            {
                return;
            }

            _done = true;
            Changes.RemoveAll(c => ReferenceEquals(c, change));
            Settled?.Invoke(change.Side, change.Dir);
        }
    }
}
