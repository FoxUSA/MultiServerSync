using System.Text.RegularExpressions;

namespace MultiServerSync.Core;

/// <summary>
/// Исключения при копировании — маски в духе .gitignore, по одной в строке (или через запятую):
/// <c>*.log</c> и <c>node_modules</c> — по имени на любой глубине; <c>cache/</c> — только папка;
/// <c>site/tmp</c> и <c>/build</c> — путь от копируемой папки (с её именем или без).
/// Регистр не важен, как в проводнике. Строки с <c>#</c> — комментарии.
/// Маска понимается так, как её имели в виду, а не буквально: <c>.env</c> ловит и <c>.env.local</c>,
/// <c>node_modules/**</c> — это вся папка node_modules на любой глубине, кавычки и «./» отбрасываются.
/// </summary>
public sealed class ExcludeRules
{
    /// <summary>Готовые шаблоны — кнопками в окне исключений.</summary>
    public static readonly string[] Templates =
        [".git", "node_modules", "__pycache__", ".venv", ".pytest_cache", "*.egg-info", ".react-router",
         "*.log", "*.tmp", ".DS_Store", "Thumbs.db", ".env"];

    /// <summary>
    /// «Стандартный набор» — одной кнопкой: то, что на сервер почти никогда не льют. Чего тут нет
    /// нарочно: .env, dist, build, vendor, bin — их как раз часто везут на сервер.
    /// </summary>
    public static readonly (string Title, string[] Masks)[] StandardSet =
    [
        ("Мусор ОС: никогда не нужен на сервере",
            ["Thumbs.db", "ehthumbs.db", "desktop.ini", ".DS_Store", "._*", ".Spotlight-V100/", ".Trashes/",
             ".directory", "$RECYCLE.BIN/"]),
        ("Временное и резервные копии редакторов",
            ["*.tmp", "*.bak", "*.swp", "*.swo", "*~", "~$*", ".~lock.*", "*.orig", "*.rej", "*.crdownload"]),
        ("Dropbox: служебное и конфликтующие копии",
            [".dropbox", ".dropbox.attr", ".dropbox.cache/", "*conflicted copy*", "*конфликтующая копия*"]),
        ("Настройки редакторов и IDE",
            [".vscode/", ".idea/", ".vs/", ".history/", ".fleet/", "*.suo", "*.user"]),
        ("Системы контроля версий",
            [".git/", ".gitignore", ".gitattributes", ".svn/", ".hg/"]),
        ("Python: кэш и окружения — на сервере ставятся заново",
            ["__pycache__/", "*.pyc", "*.pyo", ".venv/", "venv/", ".mypy_cache/", ".pytest_cache/", ".ruff_cache/",
             ".tox/", "*.egg-info/", ".coverage", "htmlcov/", ".ipynb_checkpoints/"]),
        ("JavaScript: пакеты и кэш — на сервере ставятся заново",
            ["node_modules/", ".npm/", ".pnpm-store/", ".yarn/cache/", ".parcel-cache/", ".turbo/", ".eslintcache",
             ".sass-cache/", ".nyc_output/"]),
        ("Прочее служебное разработки",
            [".gradle/", ".terraform/", ".claude/", "*.log"]),
    ];

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
            // кавычки и пробелы вокруг — от копирования из чата или .gitignore
            var mask = raw.Replace('\\', '/').Trim().Trim('"', '\'', '«', '»', '`').Trim();

            // «./build» — то же, что «/build»: от копируемой папки
            if (mask.StartsWith("./", StringComparison.Ordinal))
            {
                mask = mask[1..];
            }

            // «/build» — как в .gitignore: только в корне копируемой папки, а не на любой глубине
            var anchored = mask.StartsWith('/');

            // «**/node_modules» — имя на любой глубине, как и без «**/»
            if (mask.StartsWith("**/", StringComparison.Ordinal))
            {
                mask = mask[3..];
            }

            var dirOnly = mask.EndsWith('/');

            // «node_modules/*» и «node_modules/**» — это вся папка, где бы она ни была:
            // буквально они ловили только её содержимое и только у корня
            foreach (var tail in new[] { "/**", "/*" })
            {
                var name = mask.TrimStart('/');

                if (name.EndsWith(tail, StringComparison.Ordinal) && name[..^tail.Length] is { Length: > 0 } folder
                    && !folder.Contains('/') && !folder.Contains('*'))
                {
                    mask = anchored ? "/" + folder : folder;
                    dirOnly = true;
                    break;
                }
            }

            mask = mask.Trim('/');

            if (mask.Length == 0)
            {
                continue;
            }

            var byPath = anchored || mask.Contains('/');
            rules.Add(new Rule(ToRegex(mask), byPath, dirOnly));

            // «.env» — ещё и .env.local, .env.production: это та же тайна в другом варианте
            if (!byPath && mask.StartsWith('.') && mask.IndexOf('.', 1) < 0 && !mask.Contains('*') && !mask.Contains('?'))
            {
                rules.Add(new Rule(ToRegex(mask + ".*"), false, dirOnly));
            }
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

/// <summary>
/// Исключения одного захода: свои маски вкладки и общие. Заодно считает, что чем отсеяно —
/// для строки в выводе. Подошло под обе — засчитывается своим: их вписали для этой вкладки нарочно.
/// </summary>
public sealed class ExcludeFilter(string? own, string? global)
{
    private readonly ExcludeRules _own = ExcludeRules.Parse(own);
    private readonly ExcludeRules _global = ExcludeRules.Parse(global);

    private int _ownFiles;
    private int _ownDirs;
    private int _globalFiles;
    private int _globalDirs;

    public bool IsEmpty => _own.IsEmpty && _global.IsEmpty;

    /// <summary>Сколько всего отсеяно; папка — одна, со всем содержимым.</summary>
    public int Total => _ownFiles + _ownDirs + _globalFiles + _globalDirs;

    /// <summary>Исключён ли объект; если да — засчитывает его своим маскам или общим.</summary>
    public bool Excludes(string path, bool isDirectory)
    {
        if (_own.Matches(path, isDirectory))
        {
            Interlocked.Increment(ref isDirectory ? ref _ownDirs : ref _ownFiles);
            return true;
        }

        if (_global.Matches(path, isDirectory))
        {
            Interlocked.Increment(ref isDirectory ? ref _globalDirs : ref _globalFiles);
            return true;
        }

        return false;
    }

    /// <summary>Строка для вывода — или null, если ничего не отсеяно.</summary>
    public string? Summary()
    {
        var parts = new List<string>(2);

        if (_ownFiles + _ownDirs > 0)
        {
            parts.Add($"масками вкладки — файлов {_ownFiles}, папок {_ownDirs}");
        }

        if (_globalFiles + _globalDirs > 0)
        {
            parts.Add($"глобальными масками — файлов {_globalFiles}, папок {_globalDirs}");
        }

        return parts.Count == 0 ? null : "исключено " + string.Join("; ", parts);
    }
}
