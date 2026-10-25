using System.Text.RegularExpressions;

namespace MultiServerSync.Core;

/// <summary>
/// Исключения при копировании — маски в духе .gitignore, по одной в строке (или через запятую):
/// <c>*.log</c> и <c>node_modules</c> — по имени на любой глубине; <c>cache/</c> — только папка;
/// <c>build/**</c> или <c>site/tmp</c> — путь от копируемой папки (с её именем или без).
/// Регистр не важен, как в проводнике. Строки с <c>#</c> — комментарии.
/// </summary>
public sealed class ExcludeRules
{
    /// <summary>Готовые шаблоны — кнопками в окне исключений.</summary>
    public static readonly string[] Templates =
        [".git", "node_modules", "__pycache__", "*.log", "*.tmp", ".DS_Store", "Thumbs.db", ".env"];

    private readonly List<Rule> _rules;

    private ExcludeRules(List<Rule> rules) => _rules = rules;

    public static ExcludeRules None { get; } = new([]);

    public bool IsEmpty => _rules.Count == 0;

    /// <summary>Сколько масок в тексте — для подписи кнопки.</summary>
    public static int Count(string? text) => Split(text).Count();

    public static ExcludeRules Parse(string? text)
    {
        var rules = new List<Rule>();

        foreach (var raw in Split(text))
        {
            var mask = raw.Replace('\\', '/');
            var dirOnly = mask.EndsWith('/');
            mask = mask.Trim('/');

            if (mask.Length == 0)
            {
                continue;
            }

            rules.Add(new Rule(ToRegex(mask), mask.Contains('/'), dirOnly));
        }

        return rules.Count == 0 ? None : new ExcludeRules(rules);
    }

    /// <summary>
    /// Исключён ли объект. <paramref name="path"/> — путь от копируемого объекта включительно
    /// («site/cache/a.log»), через «/».
    /// </summary>
    public bool Matches(string path, bool isDirectory)
    {
        if (_rules.Count == 0)
        {
            return false;
        }

        var name = path[(path.LastIndexOf('/') + 1)..];
        var slash = path.IndexOf('/');
        var inside = slash < 0 ? null : path[(slash + 1)..];

        foreach (var rule in _rules)
        {
            if (rule.DirOnly && !isDirectory)
            {
                continue;
            }

            var hit = rule.ByPath
                ? rule.Pattern.IsMatch(path) || (inside is not null && rule.Pattern.IsMatch(inside))
                : rule.Pattern.IsMatch(name);

            if (hit)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> Split(string? text) =>
        (text ?? string.Empty)
            .Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(m => !m.StartsWith('#') && m.Trim('/', '\\').Length > 0);

    /// <summary>* — что угодно без «/», ** — что угодно вместе с «/», ? — один знак.</summary>
    private static Regex ToRegex(string mask)
    {
        var pattern = Regex.Escape(mask)
            .Replace(@"\*\*/", "(.*/)?")
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", "[^/]*")
            .Replace(@"\?", "[^/]");

        return new Regex("^" + pattern + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private sealed record Rule(Regex Pattern, bool ByPath, bool DirOnly);
}
