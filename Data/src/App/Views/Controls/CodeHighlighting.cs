using System.IO;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace MultiServerSync.Views.Controls;

/// <summary>
/// Подсветка для редактора «Править»: встроенные описания AvalonEdit плюс свои (bash, YAML,
/// конфиги, Dockerfile). Цвета у всех одни — как в VS Code, под тёмную или светлую тему:
/// цвет выбирается по имени («Comment», «String», «Keywords»…), а безымянные в тёмной теме
/// осветляются, чтобы не терялись на тёмном фоне.
/// </summary>
public static class CodeHighlighting
{
    private static readonly string[] Own = ["Shell", "Yaml", "Conf", "Dockerfile"];

    /// <summary>Исходные цвета: тема может смениться, а осветлять уже осветлённое нельзя.</summary>
    private static readonly Dictionary<HighlightingColor, Color?> Originals = [];

    private static bool _loaded;

    /// <summary>Описание по имени файла, а для файла без расширения — по первой строке (#!/bin/bash).</summary>
    public static IHighlightingDefinition? For(string name, string text, bool remote)
    {
        Load();

        var manager = HighlightingManager.Instance;
        var lower = name.ToLowerInvariant();
        var ext = Path.GetExtension(lower);

        if (lower is "dockerfile" or "containerfile" || lower.StartsWith("dockerfile.", StringComparison.Ordinal))
        {
            return manager.GetDefinition("Dockerfile");
        }

        if (lower is ".bashrc" or ".profile" or ".bash_profile" or ".bash_aliases" or ".bash_logout" or ".zshrc" or "makefile")
        {
            return manager.GetDefinition("Shell");
        }

        if (lower.StartsWith(".env", StringComparison.Ordinal))
        {
            return manager.GetDefinition("Conf");
        }

        if (ext is ".config" or ".csproj" or ".props" or ".targets" or ".xaml" or ".svg" or ".resx" or ".xsd" or ".xsl" or ".xslt")
        {
            return manager.GetDefinition("XML");
        }

        if (ext is ".jsonc" or ".json5")
        {
            return manager.GetDefinition("Json");
        }

        if (ext is ".ts" or ".tsx" or ".jsx" or ".mjs" or ".cjs")
        {
            return manager.GetDefinition("JavaScript");
        }

        if (ext.Length > 0 && manager.GetDefinitionByExtension(ext) is { } known)
        {
            return known;
        }

        var first = text.AsSpan(0, Math.Min(text.Length, 200));
        var shebang = first.StartsWith("#!") ? first[..Math.Max(0, first.IndexOfAny('\r', '\n') is var end and >= 0 ? end : first.Length)].ToString() : string.Empty;

        return shebang switch
        {
            _ when shebang.Contains("python", StringComparison.Ordinal) => manager.GetDefinition("Python"),
            _ when shebang.Contains("node", StringComparison.Ordinal) => manager.GetDefinition("JavaScript"),
            _ when shebang.Contains("php", StringComparison.Ordinal) => manager.GetDefinition("PHP"),
            _ when shebang.Contains("pwsh", StringComparison.Ordinal) => manager.GetDefinition("PowerShell"),
            { Length: > 0 } => manager.GetDefinition("Shell"),

            // без расширения на сервере — почти всегда конфиг: crontab, hosts, sshd_config
            _ when ext.Length == 0 && remote => manager.GetDefinition("Conf"),
            _ => null,
        };
    }

    /// <summary>Цвета всех описаний — под текущую тему. Зовётся при каждом открытии редактора.</summary>
    public static void ApplyTheme(bool dark)
    {
        Load();

        foreach (var definition in HighlightingManager.Instance.HighlightingDefinitions)
        {
            foreach (var color in Colors(definition))
            {
                Paint(color, dark);
            }
        }
    }

    private static void Load()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        var assembly = typeof(CodeHighlighting).Assembly;

        foreach (var name in Own)
        {
            using var stream = assembly.GetManifestResourceStream($"MultiServerSync.Assets.Highlighting.{name}.xshd");

            if (stream is null)
            {
                continue;
            }

            using var reader = XmlReader.Create(stream);
            var xshd = HighlightingLoader.LoadXshd(reader);
            var definition = HighlightingLoader.Load(xshd, HighlightingManager.Instance);
            HighlightingManager.Instance.RegisterHighlighting(xshd.Name, xshd.Extensions.ToArray(), definition);
        }
    }

    /// <summary>Все цвета описания: именованные и те, что заданы прямо в правилах.</summary>
    private static IEnumerable<HighlightingColor> Colors(IHighlightingDefinition definition)
    {
        var seen = new HashSet<HighlightingColor>();
        var sets = new HashSet<HighlightingRuleSet>();
        var queue = new Queue<HighlightingRuleSet>([definition.MainRuleSet]);

        foreach (var named in definition.NamedHighlightingColors)
        {
            seen.Add(named);
        }

        while (queue.Count > 0)
        {
            var set = queue.Dequeue();

            if (set is null || !sets.Add(set))
            {
                continue;
            }

            foreach (var rule in set.Rules)
            {
                Add(rule.Color);
            }

            foreach (var span in set.Spans)
            {
                Add(span.SpanColor);
                Add(span.StartColor);
                Add(span.EndColor);
                queue.Enqueue(span.RuleSet);
            }
        }

        return seen;

        void Add(HighlightingColor? color)
        {
            if (color is not null)
            {
                seen.Add(color);
            }
        }
    }

    private static void Paint(HighlightingColor color, bool dark)
    {
        if (color.IsFrozen)
        {
            return;
        }

        if (!Originals.TryGetValue(color, out var original))
        {
            original = color.Foreground?.GetColor(null);
            Originals[color] = original;
        }

        var role = Role(color.Name);
        var value = role is null
            ? original is { } own && dark ? Lighten(own) : original
            : Palette(role, dark);

        color.Foreground = value is { } c ? new SimpleHighlightingBrush(c) : null;
    }

    /// <summary>Что это за цвет — по его имени в описании.</summary>
    private static string? Role(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        bool Has(params string[] parts) => parts.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase));

        return name switch
        {
            _ when Has("Comment", "DocComment") => "comment",
            _ when Has("Added") => "added",
            _ when Has("Removed") => "removed",
            // порядок важен: NumberLiteral — число, NullOrValueKeywords — константа, ValueTypeKeywords — тип
            _ when Has("Number", "Digit", "Date") => "number",
            _ when Has("Bool", "Null", "TrueFalse", "Literal", "Constant", "This") => "constant",
            _ when Has("String", "Char", "Regex", "Code", "Entit") || name == "Value" => "string",
            _ when Has("FieldName", "Property", "Attribute", "Variable") => "property",
            _ when Has("Method", "Function", "Command") => "function",
            _ when Has("Heading", "Section", "Header", "Position") => "heading",
            _ when Has("Type", "Class", "Namespace", "Package", "Selector", "Tag", "Void") => "type",
            _ when Has("Punctuation", "Operator", "Brace", "Colon", "Slash", "Assignment") && !Has("Keyword") => "punctuation",
            _ when Has("Preprocessor") => "preprocessor",
            _ when Has("Jump", "Goto", "Loop", "Iteration", "Selection", "Control", "Exception", "Return") => "control",
            _ when Has("Keyword", "Modifier", "Visibility", "Access", "Statement", "Friend", "Get", "Context", "Unsafe", "Checked", "Semantic") => "keyword",
            _ => null,
        };
    }

    /// <summary>Тёмная — как Dark+, светлая — как Light+ в VS Code.</summary>
    private static Color? Palette(string role, bool dark) => (role, dark) switch
    {
        ("comment", true) => Hex(0x6A9955),
        ("comment", false) => Hex(0x008000),
        ("string", true) => Hex(0xCE9178),
        ("string", false) => Hex(0xA31515),
        ("number", true) => Hex(0xB5CEA8),
        ("number", false) => Hex(0x098658),
        ("constant" or "keyword", true) => Hex(0x569CD6),
        ("constant" or "keyword", false) => Hex(0x0000FF),
        ("control", true) => Hex(0xC586C0),
        ("control", false) => Hex(0xAF00DB),
        ("property", true) => Hex(0x9CDCFE),
        ("property", false) => Hex(0x001080),
        ("function", true) => Hex(0xDCDCAA),
        ("function", false) => Hex(0x795E26),
        ("type", true) => Hex(0x4EC9B0),
        ("type", false) => Hex(0x267F99),
        ("heading", true) => Hex(0x4FC1FF),
        ("heading", false) => Hex(0x0070C1),
        ("preprocessor", true) => Hex(0x9B9B9B),
        ("preprocessor", false) => Hex(0x808080),
        ("added", true) => Hex(0x89D185),
        ("added", false) => Hex(0x098658),
        ("removed", true) => Hex(0xF48771),
        ("removed", false) => Hex(0xA31515),
        _ => null, // знаки препинания — цветом текста
    };

    private static Color Hex(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    /// <summary>Тёмный цвет из светлой темы — посветлее, оттенок тот же.</summary>
    private static Color Lighten(Color c)
    {
        var max = Math.Max(c.R, Math.Max(c.G, c.B));

        if (max >= 170)
        {
            return c;
        }

        // подтягиваем к белому, сохраняя соотношение каналов
        static byte Up(byte v) => (byte)(v + (255 - v) * 0.55);
        return Color.FromRgb(Up(c.R), Up(c.G), Up(c.B));
    }
}
