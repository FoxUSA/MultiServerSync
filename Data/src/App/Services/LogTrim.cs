namespace MultiServerSync.Services;

/// <summary>
/// Обрезка лога для автокопии (и CopyPass, который её вставляет): лог в десятки тысяч строк
/// подвешивал программу, куда его вставляли. Середина вырезается — начало (команда и её старт)
/// и конец (ошибка и итог) остаются. Кнопки копируют целиком, без обрезки.
/// </summary>
public static class LogTrim
{
    /// <summary>Строк на один сервер, когда автокопия берёт «Текущий лог».</summary>
    public const int PerServer = 1000;

    /// <summary>Строк на всех вместе, когда автокопия берёт «Все логи»: каждому серверу поровну.</summary>
    public const int AllServers = 1000;

    /// <summary>Меньше этого на сервер не даём, даже когда серверов много.</summary>
    public const int MinPerServer = 100;

    /// <summary>Строк в тексте.</summary>
    public static int Count(string text) => text.Count(c => c == '\n') + 1;

    /// <summary>Сколько строк лога достаётся каждому серверу в «Все логи».</summary>
    public static int ShareOf(int servers) => Math.Max(MinPerServer, AllServers / Math.Max(1, servers));

    /// <summary>
    /// Не длиннее max строк: пятая часть — начало, остальное — конец, между ними одна строка о пропуске.
    /// </summary>
    /// <param name="button">Кнопка, которой лог копируется целиком, — для строки о пропуске.</param>
    public static string Lines(string text, int max, string button)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        if (lines.Length <= max)
        {
            return text;
        }

        var head = Math.Max(1, max / 5);
        var tail = Math.Max(1, max - head);
        var skipped = lines.Length - head - tail;

        return string.Join(Environment.NewLine, lines.Take(head))
            + Environment.NewLine
            + $"… пропущено строк: {skipped:N0} — весь лог копирует кнопка «{button}» …"
            + Environment.NewLine
            + string.Join(Environment.NewLine, lines.Skip(lines.Length - tail));
    }
}
