namespace MultiServerSync.Core;

/// <summary>
/// Фильтр «Проводника»: галочки на файлах и папках окна копирования. Два режима — «что копируется»
/// и «что не копируется», у каждого свои галочки; действует выбранный. Галочка на папке — на всю папку,
/// глубже может стоять своя. Где галочек нет, решают маски «Эта вкладка» и «Глобально»: с них
/// галочки и начинаются, а где разошлись — галочки сильнее.
/// Хранится в масках окна строками с «@» — так у фильтра та же память, что у масок: окно, отмена, файл вкладки.
/// Пути — от копируемого объекта включительно, через «/», как их видят маски.
/// </summary>
public sealed class ExplorerPicks
{
    public const char Mark = '@';

    private const string ModeKey = "@mode ";
    private const string CopiedKey = "@copied";
    private const string SkippedKey = "@skipped";

    /// <summary>Режим «Отметить, что копируется»; нет — «Отметить, что не копируется».</summary>
    public bool CopiedMode { get; set; } = true;

    /// <summary>Галочки режима «что копируется»: стоит — едет.</summary>
    public Dictionary<string, bool> Copied { get; } = new(StringComparer.Ordinal);

    /// <summary>Галочки режима «что не копируется»: стоит — не едет.</summary>
    public Dictionary<string, bool> Skipped { get; } = new(StringComparer.Ordinal);

    /// <summary>Галочки выбранного режима — только они и действуют.</summary>
    public Dictionary<string, bool> Active => CopiedMode ? Copied : Skipped;

    /// <summary>Действующих галочек нет — решают маски.</summary>
    public bool IsEmpty => Active.Count == 0;

    /// <summary>Хоть что-то отмечено в любом режиме — у кнопки фильтра зелёная плашка.</summary>
    public bool HasAny => Copied.Count + Skipped.Count > 0;

    public static bool IsPickLine(string line) => line.TrimStart().StartsWith(Mark);

    public static ExplorerPicks Parse(string? text)
    {
        var picks = new ExplorerPicks();

        foreach (var raw in (text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!IsPickLine(raw))
            {
                continue;
            }

            if (raw.StartsWith(ModeKey, StringComparison.Ordinal))
            {
                picks.CopiedMode = raw[ModeKey.Length..].Trim() != "skipped";
                continue;
            }

            // «@copied+ путь»: после ключа знак и пробел, дальше путь как есть — в нём бывают пробелы и запятые
            var space = raw.IndexOf(' ');

            if (space < 0 || raw[..space] is not { Length: > 1 } key || key[^1] is not ('+' or '-'))
            {
                continue;
            }

            var path = raw[(space + 1)..].Trim();
            var map = key[..^1] switch
            {
                CopiedKey => picks.Copied,
                SkippedKey => picks.Skipped,
                _ => null,
            };

            if (map is not null && path.Length > 0)
            {
                map[path] = key[^1] == '+';
            }
        }

        return picks;
    }

    /// <summary>Строки для масок окна; пусто — фильтра нет, и строк нет.</summary>
    public IEnumerable<string> Lines()
    {
        if (!HasAny && CopiedMode)
        {
            yield break;
        }

        yield return ModeKey + (CopiedMode ? "copied" : "skipped");

        foreach (var (key, map) in new[] { (CopiedKey, Copied), (SkippedKey, Skipped) })
        {
            foreach (var (path, ticked) in map.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                yield return $"{key}{(ticked ? '+' : '-')} {path}";
            }
        }
    }

    /// <summary>Маски окна с этим фильтром вместо прежнего: остальные строки — как были.</summary>
    public string ComposeInto(string? text)
    {
        var rest = (text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !IsPickLine(l));

        return string.Join(Environment.NewLine, rest.Concat(Lines()));
    }

    /// <summary>Ближайшая галочка выбранного режима: на самом пути или на папке выше. Null — не отмечали.</summary>
    public bool? Ticked(string path)
    {
        var map = Active;

        if (map.Count == 0)
        {
            return null;
        }

        for (var p = path; ; p = p[..p.LastIndexOf('/')])
        {
            if (map.TryGetValue(p, out var ticked))
            {
                return ticked;
            }

            if (p.LastIndexOf('/') <= 0)
            {
                return null;
            }
        }
    }

    /// <summary>Едет ли по галочкам; null — галочки тут не стоят, решают маски.</summary>
    public bool? Goes(string path) => Ticked(path) is { } ticked ? CopiedMode ? ticked : !ticked : null;

    /// <summary>Внутри папки отмечено что-то, что едет: в исключённую папку надо зайти.</summary>
    public bool AnyGoesBelow(string dir)
    {
        var prefix = dir + "/";
        return Active.Any(p => p.Key.StartsWith(prefix, StringComparison.Ordinal) && (CopiedMode ? p.Value : !p.Value));
    }

    /// <summary>Галочка на строке: на папке — на всю папку, отметки глубже снимаются.</summary>
    public void Set(string path, bool ticked)
    {
        Forget(path);
        Active[path] = ticked;
    }

    /// <summary>Отметка на строке и всё глубже — убрать: дальше решает то, что выше, или маски.</summary>
    public void Forget(string path)
    {
        var map = Active;
        var prefix = path + "/";

        foreach (var key in map.Keys.Where(k => k == path || k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            map.Remove(key);
        }
    }
}
