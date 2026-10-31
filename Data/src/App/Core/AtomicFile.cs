using System.IO;
using MultiServerSync.Services;

namespace MultiServerSync.Core;

/// <summary>
/// Файл целиком: сначала во временный рядом, потом на место, так что обрыв не портит прежний.
/// UserData лежит в папке Dropbox: синхронизация и антивирус держат файл порой по нескольку секунд, и подмена
/// получает отказ в доступе. Это ещё не ошибка: новое содержимое остаётся в памяти и пишется в фоне, а окно не ждёт.
/// Человеку говорим, только если файл занят уже минуту.
/// </summary>
public static class AtomicFile
{
    private sealed class Waiting(string text, string reason)
    {
        public string Text { get; set; } = text;

        public string Reason { get; set; } = reason;

        public DateTime Since { get; } = DateTime.UtcNow;

        public bool Reported { get; set; }
    }

    private const int RetryMs = 500;

    private static readonly TimeSpan ReportAfter = TimeSpan.FromMinutes(1);

    /// <summary>Столько выход ждёт, пока файлы освободятся.</summary>
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(5);

    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, Waiting> Queue = new();
    private static readonly System.Threading.Timer Retry = new(_ => Report(RetryQueue()));

    public static void Write(string path, string text)
    {
        lock (Gate)
        {
            // прошлое содержимое ждать больше незачем: это новее
            if (TryReplace(path, text) is not { } reason)
            {
                Queue.Remove(path);
                return;
            }

            if (Queue.TryGetValue(path, out var waiting))
            {
                waiting.Text = text;
                waiting.Reason = reason;
                return;
            }

            Queue[path] = new Waiting(text, reason);
            Retry.Change(RetryMs, Timeout.Infinite);
        }
    }

    /// <summary>Программа закрывается: дождаться занятых файлов, иначе последние изменения пропадут.</summary>
    public static void Flush()
    {
        var until = DateTime.UtcNow + ExitWait;

        while (true)
        {
            Report(RetryQueue());

            List<KeyValuePair<string, Waiting>> left;

            lock (Gate)
            {
                left = Queue.ToList();
            }

            if (left.Count == 0)
            {
                return;
            }

            if (DateTime.UtcNow >= until)
            {
                foreach (var (path, waiting) in left)
                {
                    AppLog.Error($"Не удалось сохранить {Path.GetFileName(path)}: {waiting.Reason}");
                }

                return;
            }

            Thread.Sleep(RetryMs);
        }
    }

    /// <returns>null — записано; иначе почему нет.</returns>
    private static string? TryReplace(string path, string text)
    {
        var tmp = path + ".tmp";

        try
        {
            File.WriteAllText(tmp, text);
            File.Move(tmp, path, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    /// <returns>Что сказать человеку, уже вне блокировки.</returns>
    private static List<Action> RetryQueue()
    {
        var messages = new List<Action>();

        lock (Gate)
        {
            foreach (var (path, waiting) in Queue.ToList())
            {
                var name = Path.GetFileName(path);

                if (TryReplace(path, waiting.Text) is not { } reason)
                {
                    Queue.Remove(path);

                    if (waiting.Reported)
                    {
                        messages.Add(() => AppLog.Info($"Файл {name} снова записывается, изменения сохранены"));
                    }

                    continue;
                }

                waiting.Reason = reason;

                if (!waiting.Reported && DateTime.UtcNow - waiting.Since > ReportAfter)
                {
                    waiting.Reported = true;
                    messages.Add(() => AppLog.Error($"Не удалось сохранить {name} — файл занят уже минуту, попытки продолжаются. {reason}"));
                }
            }

            if (Queue.Count > 0)
            {
                Retry.Change(RetryMs, Timeout.Infinite);
            }
        }

        return messages;
    }

    private static void Report(List<Action> messages)
    {
        foreach (var message in messages)
        {
            message();
        }
    }
}
