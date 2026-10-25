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

    /// <summary>Чьи команды идут следом — заново: сменилась галочка, строка или прогон команд.</summary>
    private void Rebind()
    {
        UnbindChain();

        _chain = _row is null ? null : AppState.Current.ChainBatchOf(_row);

        if (_chain is not null)
        {
            _chain.Results.CollectionChanged += OnChainResults;
            _after = _chain.Results.FirstOrDefault(r => ReferenceEquals(r.Server, _row!.Server));

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

        _chain = null;
        _after = null;
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppState.CopyWithChain))
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

    private void Push() => OutputColoring.SetText(_box, Compose(_row, _after));

    /// <summary>Вывод сервера и, если есть, вывод его команд после копирования — одним текстом.</summary>
    public static string Compose(OperationResult? row, OperationResult? after)
    {
        var text = row?.Output ?? string.Empty;

        if (after?.Output.TrimEnd() is not { Length: > 0 } afterText)
        {
            return text;
        }

        return text.TrimEnd() + Environment.NewLine + Environment.NewLine
            + ChainHeader + Environment.NewLine + afterText
            + (after.TimeLine is { } time ? Environment.NewLine + time : string.Empty)
            + Environment.NewLine;
    }
}
