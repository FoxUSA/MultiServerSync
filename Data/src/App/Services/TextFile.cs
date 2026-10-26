using System.Globalization;
using System.IO;
using System.Text;

namespace MultiServerSync.Services;

/// <summary>
/// Текст файла для правки — так, чтобы при сохранении нетронутое легло байт в байт: та же
/// кодировка, тот же BOM, те же концы строк (их хранит сам текст). Двоичное не правится.
/// </summary>
public sealed class TextFile
{
    /// <summary>Больше — не правка конфига, а что-то другое; редактор такое не открывает.</summary>
    public const long Limit = 10 * 1024 * 1024;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "log", "md", "markdown", "rst", "csv", "tsv",
        "json", "jsonc", "json5", "xml", "xsd", "xsl", "xslt", "xaml", "svg", "config", "csproj", "props", "targets", "resx", "sln",
        "yml", "yaml", "toml", "ini", "cfg", "conf", "cnf", "env", "properties", "vhost", "htaccess", "htpasswd",
        "service", "socket", "timer", "mount", "target", "path", "slice", "rules", "list", "repo", "sources",
        "sh", "bash", "zsh", "ksh", "fish", "ps1", "psm1", "psd1", "bat", "cmd",
        "py", "pyw", "pyi", "rb", "pl", "pm", "php", "phtml", "inc", "lua", "r", "go", "rs", "java", "kt", "kts", "swift",
        "c", "h", "cpp", "hpp", "cc", "hh", "cs", "vb", "fs", "sql",
        "js", "mjs", "cjs", "ts", "tsx", "jsx", "vue", "svelte", "css", "scss", "sass", "less", "html", "htm", "shtml",
        "tpl", "tmpl", "j2", "jinja", "twig", "tf", "tfvars", "hcl", "nix", "patch", "diff",
        "dockerfile", "containerfile", "gitignore", "gitattributes", "gitmodules", "dockerignore", "editorconfig",
        "npmrc", "nvmrc", "pem", "crt", "csr", "pub",
    };

    static TextFile() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private TextFile(string text, Encoding encoding, byte[] preamble)
    {
        Text = text;
        Encoding = encoding;
        Preamble = preamble;
    }

    public string Text { get; }

    public Encoding Encoding { get; }

    /// <summary>BOM, с которым файл был, — с ним же и запишется.</summary>
    public byte[] Preamble { get; }

    /// <summary>Подпись для строки состояния: «UTF-8», «UTF-8 с BOM», «Windows-1251».</summary>
    public string EncodingName => Encoding switch
    {
        UTF8Encoding => Preamble.Length > 0 ? "UTF-8 с BOM" : "UTF-8",
        UnicodeEncoding { CodePage: 1201 } => "UTF-16 BE",
        UnicodeEncoding => "UTF-16 LE",
        UTF32Encoding => "UTF-32",
        _ => Encoding.WebName.ToUpperInvariant(),
    };

    public static string TooBig(long size) =>
        $"файл {size / 1024 / 1024} МБ — для правки больше {Limit / 1024 / 1024} МБ не открывается";

    /// <summary>
    /// Похоже на текст, который правят руками: известное расширение, файл без расширения
    /// (на серверах так живут конфиги и скрипты) или «точечный» (.bashrc, .env).
    /// Двоичный без расширения отсеется при открытии.
    /// </summary>
    public static bool IsEditable(string name)
    {
        var ext = Path.GetExtension(name);
        return name.StartsWith('.') || ext.Length == 0 || Extensions.Contains(ext[1..]);
    }

    /// <summary>Текст из байтов. null — файл двоичный или в кодировке, которая не вернётся байт в байт.</summary>
    public static TextFile? Decode(byte[] data)
    {
        foreach (var (bom, encoding) in Boms())
        {
            if (data.AsSpan().StartsWith(bom))
            {
                return Exact(data, encoding, bom);
            }
        }

        // нулевой байт в тексте без BOM не бывает — это двоичный файл
        if (Array.IndexOf(data, (byte)0) >= 0)
        {
            return null;
        }

        return Exact(data, new UTF8Encoding(false, true), [])
            ?? Exact(data, Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback), [])
            ?? Exact(data, Encoding.GetEncoding(28591, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback), []);
    }

    public byte[] Encode(string text)
    {
        var body = Encoding.GetBytes(text);
        return Preamble.Length == 0 ? body : [.. Preamble, .. body];
    }

    /// <summary>Концы строк, какими их видно в файле: по первому переводу строки.</summary>
    public static string NewLineName(string text)
    {
        var at = text.IndexOfAny(['\r', '\n']);
        return at < 0 ? "LF" : text[at] == '\n' ? "LF" : at + 1 < text.Length && text[at + 1] == '\n' ? "CRLF" : "CR";
    }

    private static IEnumerable<(byte[] Bom, Encoding Encoding)> Boms()
    {
        // UTF-32 LE раньше UTF-16 LE: начало у них общее
        yield return ([0xFF, 0xFE, 0x00, 0x00], new UTF32Encoding(false, false, true));
        yield return ([0xEF, 0xBB, 0xBF], new UTF8Encoding(false, true));
        yield return ([0xFF, 0xFE], new UnicodeEncoding(false, false, true));
        yield return ([0xFE, 0xFF], new UnicodeEncoding(true, false, true));
    }

    /// <summary>Годится, только если текст обратно даёт те же байты: иначе сохранение что-то испортит.</summary>
    private static TextFile? Exact(byte[] data, Encoding encoding, byte[] bom)
    {
        try
        {
            var text = encoding.GetString(data, bom.Length, data.Length - bom.Length);
            var file = new TextFile(text, encoding, bom);
            return file.Encode(text).AsSpan().SequenceEqual(data) ? file : null;
        }
        catch (Exception ex) when (ex is DecoderFallbackException or EncoderFallbackException or ArgumentException)
        {
            return null;
        }
    }
}
