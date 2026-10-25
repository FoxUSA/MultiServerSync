using System.IO;
using System.Text.Json;

namespace MultiServerSync.Core;

/// <summary>
/// Версия берётся из Data\manifest.json — того же файла, что кладёт сборка.
/// В коде версия нигде не дублируется: обновили Data — обновилась и надпись в шапке.
/// </summary>
public static class AppVersion
{
    public const string ProductName = "MultiServerSync";

    static AppVersion()
    {
        Version = "без версии";
        BuiltAt = string.Empty;

        try
        {
            if (!File.Exists(AppPaths.ManifestFile))
            {
                return;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(AppPaths.ManifestFile));
            var root = doc.RootElement;

            if (root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String)
            {
                var text = version.GetString();

                if (!string.IsNullOrWhiteSpace(text))
                {
                    Version = text.Trim();
                }
            }

            if (root.TryGetProperty("built", out var built) && built.ValueKind == JsonValueKind.String)
            {
                BuiltAt = built.GetString()?.Trim() ?? string.Empty;
            }
        }
        catch
        {
            // манифест не должен мешать запуску: без него просто нет номера версии
        }
    }

    public static string Version { get; }

    /// <summary>Дата сборки из манифеста, если она там есть.</summary>
    public static string BuiltAt { get; }

    /// <summary>Надпись для заголовка окна: название и номер версии, как принято — слева в шапке.</summary>
    public static string Caption => $"{ProductName} {Version}";

    public static string Details =>
        string.IsNullOrWhiteSpace(BuiltAt) ? Version : $"{Version} · собрано {BuiltAt}";
}
