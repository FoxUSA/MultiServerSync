using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MultiServerSync.Models;
using MultiServerSync.Services;

namespace MultiServerSync.Views.Controls;

/// <summary>
/// Что показывает поле вывода: вывод выбранного сервера, а у копирования с галочкой «+ команды» —
/// следом, вживую, вывод личных команд того же сервера, тем же видом, что уходит в буфер.
/// Одно на нижнюю панель и отдельное окно вывода: поле привязывается к строке сервера
/// через <c>controls:OutputFeed.Result</c>, а текст в него кладёт <see cref="OutputColoring"/>.
/// </summary>
public sealed class OutputFeed
{
    public const string ChainHeader = "═════ Команды после копирования ═════";

    public const string CopyHeader = "═════ Копирование ═════";

    public static readonly DependencyProperty ResultProperty = DependencyProperty.RegisterAttached(
        "Result",
        typeof(OperationResult),
        typeof(OutputFeed),
        new PropertyMetadata(null, OnResultChanged));

    private static readonly DependencyProperty FeedProperty = DependencyProperty.RegisterAttached(
        "Feed",
        typeof(OutputFeed),
        typeof(OutputFeed));

    private readonly RichTextBox _box;
    private OperationResult? _row;
    private OperationBatch? _chain;
    private OperationResult? _after;
    private bool _attached;

    private OutputFeed(RichTextBox box)
    {
        _box = box;

        // поле только для чтения: история правок не нужна, а на длинном выводе каждая строка
        // записывалась в неё и сверяла выделение
        box.IsUndoEnabled = false;

        // поле живёт дольше выбора, а отдельное окно закрывается — подписки только пока поле на экране
        box.Loaded += (_, _) => Attach();
        box.Unloaded += (_, _) => Detach();

        // лупа: размер поля доходит и до уже разложенного текста — у документа он свой
        System.ComponentModel.DependencyPropertyDescriptor
            .FromProperty(Control.FontSizeProperty, typeof(RichTextBox))
            .AddValueChanged(box, (_, _) => box.Document.FontSize = box.FontSize);

        // Ctrl + колесо — та же лупа, как в браузере
        box.PreviewMouseWheel += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                AppState.Current.ZoomOutput(Math.Sign(e.Delta));
                e.Handled = true;
            }
        };
    }

    public static OperationResult? GetResult(DependencyObject d) => (OperationResult?)d.GetValue(ResultProperty);

    public static void SetResult(DependencyObject d, OperationResult? value) => d.SetValue(ResultProperty, value);

    private static void OnResultChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RichTextBox box)
        {
            return;
        }

        if (box.GetValue(FeedProperty) is not OutputFeed feed)
        {
            feed = new OutputFeed(box);
            box.SetValue(FeedProperty, feed);
        }

        feed.Detach();
        feed._row = e.NewValue as OperationResult;
        feed.Attach();
    }

    private void Attach()
    {
        if (_attached)
        {
            return;
        }

        _attached = true;
        AppState.Current.PropertyChanged += OnStateChanged;

        if (_row is not null)
        {
            _row.PropertyChanged += OnTextChanged;
        }

        Rebind();
    }

    private void Detach()
    {
        if (!_attached)
        {
            return;
        }

        _attached = false;
        AppState.Current.PropertyChanged -= OnStateChanged;

        if (_row is not null)
        {
            _row.PropertyChanged -= OnTextChanged;
        }

        UnbindChain();
    }

    /// <summary>«Не очищать»: прогон, за строками которого следим, и сами строки этого сервера.</summary>
    private OperationBatch? _wholeBatch;
    private readonly List<OperationResult> _wholeRows = [];

    /// <summary>Чьи команды идут следом — заново: сменилась галочка, строка или прогон команд.</summary>
    private void Rebind()
    {
        UnbindChain();

        // «Не очищать» — весь вывод сервера по строкам журнала подряд: следим за всеми его строками
        if (AppState.Current.OutputWhole && _row?.Batch is { } batch)
        {
            _wholeBatch = batch;
            batch.Results.CollectionChanged += OnChainResults;

            foreach (var row in batch.AllRows.Where(r => r.Server.Id == _row.Server.Id && !ReferenceEquals(r, _row)))
            {
                row.PropertyChanged += OnTextChanged;
                _wholeRows.Add(row);
            }

            Push();
            return;
        }

        _chain = _row is null ? null : AppState.Current.ChainBatchOf(_row);

        if (_chain is not null)
        {
            _chain.Results.CollectionChanged += OnChainResults;
            _after = AppState.Current.AfterRowOf(_row!);

            if (_after is not null)
            {
                _after.PropertyChanged += OnTextChanged;
            }
        }

        Push();
    }

    private void UnbindChain()
    {
        if (_chain is not null)
        {
            _chain.Results.CollectionChanged -= OnChainResults;
        }

        if (_after is not null)
        {
            _after.PropertyChanged -= OnTextChanged;
        }

        if (_wholeBatch is not null)
        {
            _wholeBatch.Results.CollectionChanged -= OnChainResults;
        }

        foreach (var row in _wholeRows)
        {
            row.PropertyChanged -= OnTextChanged;
        }

        _wholeRows.Clear();
        _wholeBatch = null;
        _chain = null;
        _after = null;
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppState.CopyWithChain) or nameof(AppState.OutputWhole))
        {
            Rebind();
        }
    }

    private void OnChainResults(object? sender, NotifyCollectionChangedEventArgs e) => Rebind();

    private void OnTextChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OperationResult.Output) or nameof(OperationResult.TimeLine))
        {
            Push();
        }
    }

    private void Push()
    {
        // «Не очищать»: строки этого сервера подряд, от ранней к поздней, — как один терминал
        if (_wholeBatch is not null && _row is not null)
        {
            var rows = _wholeBatch.AllRows.Where(r => r.Server.Id == _row.Server.Id).ToList();
            OutputColoring.SetText(_box, string.Join(Environment.NewLine + Environment.NewLine,
                rows.Select(r => Compose(r, null)).Where(t => t.Trim().Length > 0)));
            return;
        }

        OutputColoring.SetText(_box, Compose(_row, _after));
    }

    /// <summary>
    /// Вывод сервера и, если есть, вывод его команд после копирования — одним текстом.
    /// Один вид на всё: тем же текстом вывод показан в терминале и уходит в буфер.
    /// </summary>
    /// <param name="maxLines">Автокопия: не длиннее стольких строк, середина вырезается.</param>
    /// <param name="trimmedBy">Какая кнопка отдаст лог целиком — для пометки о вырезанном.</param>
    public static string Compose(OperationResult? row, OperationResult? after, int? maxLines = null, string trimmedBy = "")
    {
        if (row is null)
        {
            return string.Empty;
        }

        var text = row.Report(maxLines, trimmedBy);

        return after is null || after.Output.TrimEnd().Length == 0
            ? text
            : CopyHeader + Environment.NewLine + text + Environment.NewLine
                + ChainHeader + Environment.NewLine + after.Report(maxLines, trimmedBy);
    }
}
