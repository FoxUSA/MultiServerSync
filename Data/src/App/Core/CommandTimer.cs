using System.Globalization;
using System.Text.RegularExpressions;

namespace MultiServerSync.Core;

/// <summary>
/// Есть ли в команде таймер, по которому она закончится: <c>sleep N</c> и <c>timeout N команда</c>.
/// Считается сумма. Текст в кавычках, комментарии и фоновое (<c>sleep 5 &amp;</c>) не считаются; в цикле
/// сколько пройдёт времени заранее неизвестно, поэтому таймера нет. Не распознали — лучше без таймера, чем с неверным.
/// </summary>
public static class CommandTimer
{
    private static readonly Regex Quoted = new("""'[^']*'|"(?:[^"\\]|\\.)*"|\\\r?\n""", RegexOptions.Compiled);

    private static readonly Regex Comment = new(@"(^|\s)#[^\n]*", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>«2&gt;&amp;1» и «&amp;&gt;файл» — перенаправление, а не запуск в фоне.</summary>
    private static readonly Regex Redirect = new(@"\d*[<>]&\d*|&>>?", RegexOptions.Compiled);

    private static readonly Regex Separator = new(@"&&|\|\||[;|&\n]", RegexOptions.Compiled);

    private static readonly Regex Duration = new(@"^(\d+(?:\.\d+)?)([smhd]?)$", RegexOptions.Compiled);

    /// <summary>Сколько секунд команда проведёт по таймерам; 0 — таймера нет.</summary>
    public static int Seconds(string command)
    {
        var text = Quoted.Replace(command ?? string.Empty, " ");
        text = Comment.Replace(text, string.Empty);
        text = Redirect.Replace(text, " ") + "\n";

        var total = 0d;
        var from = 0;

        foreach (Match separator in Separator.Matches(text))
        {
            var words = Words(text[from..separator.Index]);
            from = separator.Index + separator.Length;

            if (words.Count > 0 && words[0] is "for" or "while" or "until" or "select")
            {
                return 0;
            }

            if (separator.Value != "&")
            {
                total += OfSegment(words);
            }
        }

        return (int)Math.Ceiling(total);
    }

    private static List<string> Words(string segment)
    {
        // скобки вокруг: «(sleep 3)», «{ sleep 3; }»
        var words = segment.Trim().Trim('(', ')', '{', '}').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();

        // что стоит перед самой командой
        while (words.Count > 0)
        {
            if (words[0] is "then" or "else" or "exec" or "time" or "!")
            {
                words.RemoveAt(0);
            }
            else if (words[0] == "sudo")
            {
                words.RemoveAt(0);

                while (words.Count > 0 && words[0].StartsWith('-'))
                {
                    var takesValue = words[0] is "-u" or "-g";
                    words.RemoveAt(0);

                    if (takesValue && words.Count > 0)
                    {
                        words.RemoveAt(0);
                    }
                }
            }
            else
            {
                break;
            }
        }

        return words;
    }

    private static double OfSegment(List<string> words)
    {
        if (words.Count < 2)
        {
            return 0;
        }

        if (words[0] == "sleep")
        {
            // sleep складывает все свои числа: «sleep 1m 30s»
            return words.Skip(1).Select(Parse).TakeWhile(s => s is not null).Sum(s => s!.Value);
        }

        if (words[0] != "timeout")
        {
            return 0;
        }

        for (var i = 1; i < words.Count; i++)
        {
            if (!words[i].StartsWith('-'))
            {
                return Parse(words[i]) ?? 0;
            }

            // значение отдельным словом: «-k 5», «-s KILL»
            if (words[i] is "-k" or "-s" or "--kill-after" or "--signal")
            {
                i++;
            }
        }

        return 0;
    }

    private static double? Parse(string word)
    {
        var match = Duration.Match(word);

        if (!match.Success)
        {
            return null;
        }

        var value = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);

        return match.Groups[2].Value switch
        {
            "m" => value * 60,
            "h" => value * 3600,
            "d" => value * 86400,
            _ => value,
        };
    }
}
