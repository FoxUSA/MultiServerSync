using System.IO.Enumeration;

namespace MultiServerSync.Core;

/// <summary>
/// Маска имени в фильтрах обзора: «*.conf» и «site?» — как в проводнике, без звёздочки —
/// просто часть имени («nginx»). Несколько масок — через пробел, запятую или «;».
/// Регистр не важен.
/// </summary>
public static class NameMask
{
    private static readonly char[] Separators = [' ', ',', ';'];

    public static bool IsEmpty(string? mask) => string.IsNullOrWhiteSpace(mask);

    /// <summary>Маска как шаблоны имени для find -iname: часть без звёздочки становится «*часть*».</summary>
    public static IReadOnlyList<string> Patterns(string? mask) => IsEmpty(mask)
        ? []
        : mask!.Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Contains('*') || p.Contains('?') ? p : "*" + p + "*")
            .ToList();

    /// <summary>
    /// Поиск в тексте, а не в имени: набранное — одна маска целиком, пробелы её не делят
    /// («принял соединение» — фраза). Со звёздочкой и «?» совпадает любая часть текста.
    /// </summary>
    public static bool FoundIn(string text, string? mask)
    {
        if (IsEmpty(mask))
        {
            return true;
        }

        var part = mask!.Trim();

        // обратная косая в маске — знак экранирования, а в тексте бывают пути Windows
        return part.Contains('*') || part.Contains('?')
            ? FileSystemName.MatchesSimpleExpression("*" + part.Replace("\\", "\\\\") + "*", text, ignoreCase: true)
            : text.Contains(part, StringComparison.OrdinalIgnoreCase);
    }

    public static bool Matches(string name, string? mask)
    {
        if (IsEmpty(mask))
        {
            return true;
        }

        foreach (var part in mask!.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var hit = part.Contains('*') || part.Contains('?')
                ? FileSystemName.MatchesSimpleExpression(part, name, ignoreCase: true)
                : name.Contains(part, StringComparison.OrdinalIgnoreCase);

            if (hit)
            {
                return true;
            }
        }

        return false;
    }
}
