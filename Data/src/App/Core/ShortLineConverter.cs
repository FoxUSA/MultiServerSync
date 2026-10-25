using System.Globalization;
using System.Windows.Data;

namespace MultiServerSync.Core;

/// <summary>
/// Строка команды для выпадающего списка. Многострочный скрипт схлопывается в первую строку,
/// длинная строка обрезается: иначе одна вставленная портянка растягивает список на весь экран
/// в высоту, а однострочник на несколько килобайт — ещё и в ширину.
/// </summary>
public sealed class ShortLineConverter : IValueConverter
{
    /// <summary>Сколько символов оставить: дальше строка в списке всё равно не читается.</summary>
    public int Limit { get; set; } = 160;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Shorten(value as string ?? string.Empty, Limit);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;

    /// <summary>Первая непустая строка, обрезанная по длине, плюс счётчик остальных строк.</summary>
    public static string Shorten(string text, int limit = 160)
    {
        var lines = text
            .Split('\n')
            .Select(l => l.Trim('\r', ' ', '\t'))
            .Where(l => l.Length > 0)
            .ToList();

        if (lines.Count == 0)
        {
            return string.Empty;
        }

        var head = lines[0].Length > limit ? lines[0][..limit] + "…" : lines[0];

        return lines.Count == 1 ? head : head + $" … (строк: {lines.Count})";
    }
}
