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
