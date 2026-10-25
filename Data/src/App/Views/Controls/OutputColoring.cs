using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace MultiServerSync.Views.Controls;

/// <summary>
/// Вывод сервера в поле только для чтения, с подсветкой строк: отправленный файл — зелёным,
/// пропуски — приглушённо, ошибки — красным. В простом TextBox цвет строки не задать,
/// поэтому вывод кладётся в RichTextBox, а привязка идёт к тексту через это свойство.
///
/// Вывод только дописывается, поэтому новые строки добавляются к уже показанным:
/// перестраивать весь документ на каждую строку — заметно тормозит на длинном выводе.
/// Целиком документ собирается заново, только когда текст сменился совсем (другой сервер,
/// очистка).
/// </summary>
public static class OutputColoring
{
    private static readonly SolidColorBrush Sent = Freeze(Color.FromRgb(0x2D, 0xB0, 0x55));
    private static readonly SolidColorBrush Error = Freeze(Color.FromRgb(0xE8, 0x5C, 0x5C));
    private static readonly SolidColorBrush Warning = Freeze(Color.FromRgb(0xE8, 0x9A, 0x3C));

    /// <summary>Начало строки-предупреждения: ничего не сломалось, но сделано не всё, что просили.</summary>
    public const string WarningMark = "— ВНИМАНИЕ: ";

    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text",
        typeof(string),
        typeof(OutputColoring),
        new PropertyMetadata(string.Empty, OnTextChanged));

    /// <summary>Сколько текста уже разложено по документу — с этого места дописывается новое.</summary>
    private static readonly DependencyProperty ShownProperty = DependencyProperty.RegisterAttached(
        "Shown",
        typeof(string),
        typeof(OutputColoring),
        new PropertyMetadata(string.Empty));

    public static string GetText(DependencyObject d) => (string)d.GetValue(TextProperty);

    public static void SetText(DependencyObject d, string value) => d.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RichTextBox box)
        {
            return;
        }

        var text = e.NewValue as string ?? string.Empty;
        var shown = (string)box.GetValue(ShownProperty);
        var paragraph = box.Document.Blocks.FirstBlock as Paragraph;

        // дописываем, только если прежний текст остался началом нового и кончался целой строкой
        var append = paragraph is not null
            && shown.Length > 0
            && text.Length >= shown.Length
            && shown.EndsWith('\n')
            && text.StartsWith(shown, StringComparison.Ordinal);

        if (!append)
        {
            paragraph = new Paragraph { Margin = new Thickness(0) };

            box.Document = new FlowDocument(paragraph)
            {
                PagePadding = new Thickness(0),
                FontFamily = box.FontFamily,
                FontSize = box.FontSize,
            };

            shown = string.Empty;
        }

        AddLines(paragraph!, text[shown.Length..]);
        box.SetValue(ShownProperty, text);

        // за новыми строками прокручиваемся, пока пользователь ничего не выделил
        if (box.Selection.IsEmpty)
        {
            box.ScrollToEnd();
        }
    }

    private static void AddLines(Paragraph paragraph, string chunk)
    {
        if (chunk.Length == 0)
        {
            return;
        }

        var lines = chunk.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        // последний кусок после завершающего перевода строки пустой — строки там нет
        var count = chunk.EndsWith('\n') ? lines.Length - 1 : lines.Length;

        for (var i = 0; i < count; i++)
        {
            paragraph.Inlines.Add(Paint(new Run(lines[i]), lines[i]));
            paragraph.Inlines.Add(new LineBreak());
        }
    }

    private static Run Paint(Run run, string line)
    {
        // отправленный файл — и в ходе копирования, и в списке под итогом
        if (line.StartsWith("→ ", StringComparison.Ordinal))
        {
            run.Foreground = Sent;
            run.FontWeight = FontWeights.SemiBold;
        }
        else if (line.StartsWith("── ", StringComparison.Ordinal))
        {
            run.FontWeight = FontWeights.SemiBold;
        }
        else if (line.StartsWith("пропуск (", StringComparison.Ordinal))
        {
            run.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorTertiaryBrush");
        }
        else if (line.StartsWith("замена (", StringComparison.Ordinal))
        {
            run.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorSecondaryBrush");
        }
        else if (line.StartsWith(WarningMark, StringComparison.Ordinal))
        {
            run.Foreground = Warning;
            run.FontWeight = FontWeights.SemiBold;
        }
        else if (line.StartsWith("ОШИБКА", StringComparison.Ordinal)
            || line.StartsWith("РАСХОЖДЕНИЕ", StringComparison.Ordinal)
            || line.StartsWith("НЕ СВЕРЕНО", StringComparison.Ordinal))
        {
            run.Foreground = Error;
        }

        return run;
    }

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
