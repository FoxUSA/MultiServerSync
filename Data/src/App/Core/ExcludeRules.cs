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

    /// <summary>Строка «Включить» в тексте масок вкладки: с этим знаком в начале.</summary>
    public const char IncludeMark = '+';

    /// <summary>Сколько масок в тексте, вместе с «Включить», — для подписи кнопки.</summary>
    public static int Count(string? text) => Entries(text).Count();

    /// <summary>Маски «Исключить»; строки «Включить» пропускаются.</summary>
    public static ExcludeRules Parse(string? text) => Build(Entries(text).Where(e => !e.Include).Select(e => e.Mask));

    /// <summary>Маски «Включить»: строки со знаком <see cref="IncludeMark"/> в начале.</summary>
    public static ExcludeRules ParseIncludes(string? text) => Build(Entries(text).Where(e => e.Include).Select(e => e.Mask));

    private static ExcludeRules Build(IEnumerable<string> masks)
    {
        var rules = new List<Rule>();

        foreach (var raw in masks)
        {
            if (Normalize(raw) is not { } m)
            {
                continue;
            }

            rules.Add(new Rule(ToRegex(m.Mask), m.ByPath, m.DirOnly, m.Mask));

            if (m.WithVariants)
            {
                rules.Add(new Rule(ToRegex(m.Mask + ".*"), false, m.DirOnly, m.Mask + ".*"));
            }
        }

        return rules.Count == 0 ? None : new ExcludeRules(rules);
    }

    /// <summary>Маски текста по одной — так, как их разберёт <see cref="Parse"/>: строки и запятые, без комментариев.</summary>
    public static IEnumerable<string> Masks(string? text) => Split(text);

    /// <summary>
    /// Подходит сам объект или папка, в которой он лежит. Нужно, когда в исключённую папку всё же заходят
    /// ради «Включить»: её содержимое остаётся исключённым, хотя по имени под маску не подходит.
    /// </summary>
    public bool MatchesSelfOrAncestor(string path, bool isDirectory)
    {
        if (Matches(path, isDirectory))
        {
            return true;
        }

        for (var slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1))
        {
            if (Matches(path[..slash], isDirectory: true))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Может ли что-то внутри папки подойти под эти маски: тогда в исключённую папку надо зайти.
    /// Маска по имени ловит на любой глубине — заходить надо всегда; маска по пути — если папка лежит на этом пути.
    /// </summary>
    public bool MayMatchInside(string dir)
    {
        if (MatchesSelfOrAncestor(dir, isDirectory: true))
        {
            return true;
        }

        var segments = dir.Split('/');

        foreach (var rule in _rules)
        {
            if (!rule.ByPath)
            {
                return true;
            }

            // путь маски считается от копируемого объекта — с его именем или без, как в Matches
            var mask = rule.Mask.Split('/');

            if (PrefixFits(mask, segments) || (segments.Length > 1 && PrefixFits(mask, segments[1..])))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Начало маски совпадает с папкой, и в маске есть что-то глубже неё.</summary>
    private static bool PrefixFits(string[] mask, string[] dir)
    {
        for (var i = 0; i < dir.Length; i++)
        {
            if (i >= mask.Length)
            {
                return false;
            }

            if (mask[i].Contains("**", StringComparison.Ordinal))
            {
                return true;
            }

            if (!ToRegex(mask[i]).IsMatch(dir[i]))
            {
                return false;
            }
        }

        return mask.Length > dir.Length;
    }

    /// <summary>
    /// Как понята маска — для таблицы окна исключений: где ищется («везде», «в корне», «по пути»),
    /// что ловит («всё» или «папки») и ловит ли ещё варианты вроде .env.local. Null — маска пустая.
    /// </summary>
    public static (string Where, string What, bool Variants)? Explain(string raw)
    {
        if (Normalize(raw) is not { } m)
        {
            return null;
        }

        var where = m.ByPath ? m.Mask.Contains('/') ? "по пути" : "в корне" : "везде";
        return (where, m.DirOnly ? "папки" : "всё", m.WithVariants);
    }

    /// <summary>Маска, какой её имели в виду; null — пустая.</summary>
    private static (string Mask, bool ByPath, bool DirOnly, bool WithVariants)? Normalize(string raw)
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
            return null;
        }

        var byPath = anchored || mask.Contains('/');

        // «.env» — ещё и .env.local, .env.production: это та же тайна в другом варианте
        var variants = !byPath && mask.StartsWith('.') && mask.IndexOf('.', 1) < 0 && !mask.Contains('*') && !mask.Contains('?');

        return (mask, byPath, dirOnly, variants);
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

    private static IEnumerable<string> Split(string? text) => Entries(text).Select(e => e.Mask);

    /// <summary>
    /// Маски текста: строки и запятые, без комментариев. Знак «Включить» стоит в начале строки
    /// и действует на все маски этой строки через запятую.
    /// </summary>
    private static IEnumerable<(string Mask, bool Include)> Entries(string? text)
    {
        foreach (var line in (text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // «#» — комментарий, «@» — галочки «Проводника» (ExplorerPicks): это не маски
            if (line.StartsWith('#') || ExplorerPicks.IsPickLine(line))
            {
                continue;
            }

            var include = line.StartsWith(IncludeMark);
            var body = include ? line[1..] : line;

            foreach (var mask in body.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!mask.StartsWith('#') && mask.Trim('/', '\\').Length > 0)
                {
                    yield return (mask, include);
                }
            }
        }
    }

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

    private sealed record Rule(Regex Pattern, bool ByPath, bool DirOnly, string Mask);
}

/// <summary>
/// Исключения одного захода: свои маски вкладки и общие. Заодно считает, что чем отсеяно —
/// для строки в выводе. Подошло под обе — засчитывается своим: их вписали для этой вкладки нарочно.
/// </summary>
public sealed class ExcludeFilter(string? own, string? global)
{
    private readonly ExcludeRules _own = ExcludeRules.Parse(own);
    private readonly ExcludeRules _global = ExcludeRules.Parse(global);

    /// <summary>«Включить» — только свои у вкладки: что едет, даже если попало под исключение.</summary>
    private readonly ExcludeRules _include = ExcludeRules.ParseIncludes(own);

    private int _ownFiles;
    private int _ownDirs;
    private int _globalFiles;
    private int _globalDirs;
    private int _returned;

    /// <summary>Галочки «Проводника» — свои у окна, сильнее масок.</summary>
    private readonly ExplorerPicks _picks = ExplorerPicks.Parse(own);

    private int _pickFiles;
    private int _pickDirs;

    public bool IsEmpty => _own.IsEmpty && _global.IsEmpty && _picks.IsEmpty;

    /// <summary>Сколько всего отсеяно; папка — одна, со всем содержимым.</summary>
    public int Total => _ownFiles + _ownDirs + _globalFiles + _globalDirs + _pickFiles + _pickDirs;

    /// <summary>
    /// Исключён ли объект; если да — засчитывает его галочкам, своим маскам или общим.
    /// Галочки «Проводника» сильнее масок, «Включить» сильнее «Исключить». Исключённая папка, внутрь которой
    /// указывает галочка или «Включить», не пропускается: в неё заходят и берут только то, что едет.
    /// Сама она создаётся, только если в ней что-то едет (<see cref="OnlyForIncludes"/>).
    /// </summary>
    public bool Excludes(string path, bool isDirectory)
    {
        if (_include.IsEmpty && _picks.IsEmpty)
        {
            // исключённая папка не обходится, так что родителей проверять незачем
            return Count(_own.Matches(path, isDirectory), _global.Matches(path, isDirectory), isDirectory);
        }

        var v = Decide(path, isDirectory);

        if (v.Returned)
        {
            Interlocked.Increment(ref _returned);
            return false;
        }

        if (!v.Drop || v.Enter)
        {
            return false;
        }

        if (v.ByPick)
        {
            Interlocked.Increment(ref isDirectory ? ref _pickDirs : ref _pickFiles);
            return true;
        }

        return Count(v.Own, v.Global, isDirectory);
    }

    /// <summary>Папка исключена, но в неё заходят ради галочек или «Включить»: создавать её, только если в ней что-то едет.</summary>
    public bool OnlyForIncludes(string path) =>
        !(_include.IsEmpty && _picks.IsEmpty) && Decide(path, isDirectory: true) is { Drop: true, Enter: true };

    /// <summary>Исключено масками и «Включить» — без галочек «Проводника»: с этого галочки начинаются.</summary>
    public bool DroppedByMasks(string path, bool isDirectory)
    {
        if (!_own.MatchesSelfOrAncestor(path, isDirectory) && !_global.MatchesSelfOrAncestor(path, isDirectory))
        {
            return false;
        }

        return !_include.MatchesSelfOrAncestor(path, isDirectory);
    }

    private readonly record struct Verdict(bool Drop, bool Enter, bool ByPick, bool Own, bool Global, bool Returned);

    private Verdict Decide(string path, bool isDirectory)
    {
        // галочка решает за себя и за всё внутри, пока глубже не стоит своя
        if (_picks.Goes(path) is { } goes)
        {
            return goes
                ? default
                : new Verdict(Drop: true, Enter: isDirectory && _picks.AnyGoesBelow(path), ByPick: true, false, false, false);
        }

        var own = _own.MatchesSelfOrAncestor(path, isDirectory);
        var global = !own && _global.MatchesSelfOrAncestor(path, isDirectory);

        if (!own && !global)
        {
            return default;
        }

        if (!isDirectory && _include.MatchesSelfOrAncestor(path, isDirectory: false))
        {
            return new Verdict(false, false, false, false, false, Returned: true);
        }

        var enter = isDirectory && (_include.MayMatchInside(path) || _picks.AnyGoesBelow(path));
        return new Verdict(Drop: true, enter, ByPick: false, own, global, false);
    }

    private bool Count(bool own, bool global, bool isDirectory)
    {
        if (own)
        {
            Interlocked.Increment(ref isDirectory ? ref _ownDirs : ref _ownFiles);
            return true;
        }

        if (global)
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

        if (_pickFiles + _pickDirs > 0)
        {
            parts.Add($"галочками «Проводника» — файлов {_pickFiles}, папок {_pickDirs}");
        }

        if (_ownFiles + _ownDirs > 0)
        {
            parts.Add($"масками вкладки — файлов {_ownFiles}, папок {_ownDirs}");
        }

        if (_globalFiles + _globalDirs > 0)
        {
            parts.Add($"глобальными масками — файлов {_globalFiles}, папок {_globalDirs}");
        }

        if (_returned > 0)
        {
            parts.Add($"«Включить» вернуло файлов {_returned}");
        }

        return parts.Count == 0 ? null : "исключено " + string.Join("; ", parts);
    }
}
