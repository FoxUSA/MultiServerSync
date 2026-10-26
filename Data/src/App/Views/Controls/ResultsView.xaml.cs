using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using MultiServerSync.Models;
using MultiServerSync.Services;
using MultiServerSync.Views.Dialogs;
using Wpf.Ui.Controls;

namespace MultiServerSync.Views.Controls;

public partial class ResultsView : UserControl
{
    public ResultsView()
    {
        InitializeComponent();

        // «Всем серверам» в строке ответа берёт серверы показанного прогона
        Answer.BatchSource = () => Batch;

        DataContextChanged += OnDataContextChanged;

        // порядок в таблице выбирается щелчком по заголовку и не сбрасывается; строка команд
        // после копирования в сортировке не участвует — стоит под строкой своего копирования
        GridSort.Keep(ResultsGrid, "results",
            byColumn => ReferenceEquals(ResultsGrid.ItemsSource, _rows) ? new PairOrder(byColumn, _heads, _queue) : null);

        // вывод может жить в отдельном окне: панель тогда пустая, а кнопка возвращает его назад
        OutputWindow.OpenChanged += ApplyDetachState;
        ApplyDetachState();

        // «Прошло» тикает раз в секунду — только у показанного прогона и пока таблицу видно
        var agoTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        agoTimer.Tick += (_, _) =>
        {
            if (IsVisible && Batch is { } batch)
            {
                foreach (var result in batch.Results)
                {
                    result.RaiseAgo();
                }
            }
        };
        Loaded += (_, _) => agoTimer.Start();
        Unloaded += (_, _) => agoTimer.Stop();
    }

    private OperationBatch? Batch => DataContext as OperationBatch;

    /// <summary>
    /// Строка кнопок стоит в одной строке с ярлыками «Терминал / Журнал»: слева отступ
    /// на их ширину, высота — как у них, чтобы кнопки встали по их середине.
    /// </summary>
    public void AlignToolbar(double indent, double height)
    {
        ToolBar.Margin = new Thickness(indent, 0, 0, 4);
        ToolBar.MinHeight = height;
    }

    /// <summary>Команды после копирования показанного прогона; null — их нет.</summary>
    private OperationBatch? _chain;

    /// <summary>Строки таблицы, когда у копирования есть команды после него: сервер, под ним его команды.</summary>
    private readonly ObservableCollection<OperationResult> _rows = new();

    /// <summary>Строка команд → строка её копирования.</summary>
    private readonly Dictionary<OperationResult, OperationResult> _heads = new(ReferenceEqualityComparer.Instance);

    /// <summary>Место строки копирования в очереди — порядок, когда по столбцу строки равны.</summary>
    private readonly Dictionary<OperationResult, int> _queue = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Порядок связками: копирования сравниваются по выбранному столбцу, строка команд
    /// идёт сразу под своим копированием, как бы ни менялись её собственные значения.
    /// </summary>
    private sealed class PairOrder(
        System.Collections.IComparer byColumn,
        Dictionary<OperationResult, OperationResult> heads,
        Dictionary<OperationResult, int> queue) : System.Collections.IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (ReferenceEquals(x, y) || x is not OperationResult a || y is not OperationResult b)
            {
                return 0;
            }

            var headA = heads.GetValueOrDefault(a, a);
            var headB = heads.GetValueOrDefault(b, b);

            if (!ReferenceEquals(headA, headB))
            {
                var order = byColumn.Compare(headA, headB);
                return order != 0 ? order : queue.GetValueOrDefault(headA).CompareTo(queue.GetValueOrDefault(headB));
            }

            return ReferenceEquals(a, headA) ? -1 : 1;
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is OperationBatch old)
        {
            old.Results.CollectionChanged -= OnResultsChanged;
        }

        ShowRows();

        // окно вывода пишет состояние того прогона, что показывает панель
        OutputWindow.TrackBatch(e.NewValue as OperationBatch);

        // другой прогон — другие имена серверов: ширина столбца по ним
        FitServerColumn();

        if (e.NewValue is OperationBatch batch)
        {
            batch.Results.CollectionChanged += OnResultsChanged;

            // сводка приходит уже заполненной — события не будет, выбираем сами
            if (batch.Results.Count > 0)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ResultsGrid.SelectedItem is null && ReferenceEquals(DataContext, batch) && batch.Results.Count > 0)
                    {
                        ResultsGrid.SelectedItem = batch.Results[0];
                    }
                }));
            }
        }
    }

    /// <summary>
    /// Копирование с командами после него: под строкой копирования каждого сервера — строка
    /// его команд, та же, что в разделе «Команда». Команды прошлого прогона не показываются:
    /// пока идёт новое копирование, они ещё не начались.
    /// </summary>
    private void ShowRows()
    {
        var chain = Batch is { } batch ? AppState.Current.AfterCopyBatchOf(batch) : null;

        if (!ReferenceEquals(chain, _chain))
        {
            if (_chain is not null)
            {
                _chain.Results.CollectionChanged -= OnChainChanged;
                _chain.PropertyChanged -= OnChainProperty;
            }

            _chain = chain;

            if (_chain is not null)
            {
                _chain.Results.CollectionChanged += OnChainChanged;
                _chain.PropertyChanged += OnChainProperty;
            }
        }

        if (Batch is not { } shown)
        {
            ResultsGrid.ItemsSource = null;
            return;
        }

        if (_chain is null)
        {
            if (!ReferenceEquals(ResultsGrid.ItemsSource, shown.Results))
            {
                ResultsGrid.ItemsSource = shown.Results;
            }

            return;
        }

        var picked = ResultsGrid.SelectedItem;
        var fresh = _chain.StartedAt is { } chainStart && shown.StartedAt is { } copyStart && chainStart >= copyStart;

        _rows.Clear();
        _heads.Clear();
        _queue.Clear();

        var pairs = shown.Results
            .Select(row => (row, after: fresh ? _chain.Results.FirstOrDefault(c => ReferenceEquals(c.Server, row.Server)) : null))
            .ToList();

        // связки заполняются раньше строк: таблица ставит строку на место уже при добавлении
        foreach (var (row, after) in pairs)
        {
            _queue[row] = _queue.Count;

            if (after is not null)
            {
                _heads[after] = row;
            }
        }

        foreach (var (row, after) in pairs)
        {
            _rows.Add(row);

            if (after is not null)
            {
                _rows.Add(after);
            }
        }

        if (!ReferenceEquals(ResultsGrid.ItemsSource, _rows))
        {
            ResultsGrid.ItemsSource = _rows;
        }

        if (picked is not null && _rows.Contains(picked))
        {
            ResultsGrid.SelectedItem = picked;
        }

        FitServerColumn();
    }

    private void OnChainChanged(object? sender, NotifyCollectionChangedEventArgs e) => Dispatcher.BeginInvoke(new Action(ShowRows));

    private void OnChainProperty(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OperationBatch.StartedAt))
        {
            Dispatcher.BeginInvoke(new Action(ShowRows));
        }
    }

    /// <summary>Первый сервер выделяется сам, чтобы панель вывода не пустовала.</summary>
    private void OnResultsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // новый прогон: команды после копирования могли включить или выключить
        ShowRows();

        if (ResultsGrid.SelectedItem is null && Batch is { Results.Count: > 0 } batch)
        {
            ResultsGrid.SelectedItem = batch.Results[0];
        }

        FitServerColumn();
    }

    /// <summary>
    /// «Сервер» — по содержимому. Ширина «Auto» у таблицы WPF только растёт: однажды показанное
    /// длинное имя (с названием вкладки) оставляло столбец широким и для коротких. Поэтому при
    /// каждой смене списка ширина сбрасывается и меряется заново по тому, что показано сейчас.
    /// </summary>
    private void FitServerColumn() => Dispatcher.BeginInvoke(new Action(() =>
    {
        ServerColumn.Width = new DataGridLength(0);
        ServerColumn.Width = DataGridLength.Auto;
    }), System.Windows.Threading.DispatcherPriority.Background);

    /// <summary>Выбранный сервер — тот, чей вывод показывает и отдельное окно.</summary>
    private void OnResultPicked(object sender, SelectionChangedEventArgs e)
    {
        OutputWindow.Track(ResultsGrid.SelectedItem as OperationResult);

        // прогон помнит свой выбранный сервер — для автокопии, когда терминал смотрит в другое место
        if (Batch is { } batch && ResultsGrid.SelectedItem is OperationResult picked)
        {
            batch.Picked = picked;
        }
    }

    /// <summary>Корзина в заголовке вывода: убирает накопленный вывод выбранного сервера.</summary>
    private void OnClearOutput(object sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is not OperationResult result)
        {
            AppLog.Hint("Сначала выберите сервер в таблице слева — очищается вывод выбранного.", LogLevel.Warning);
            return;
        }

        if (result.Output.Length == 0)
        {
            AppLog.Hint($"У сервера {result.Title} вывода и так нет.");
            return;
        }

        result.ClearOutput();
        AppLog.Hint($"Вывод очищен: {result.Title}.");
    }

    /// <summary>
    /// Кнопка в заголовке вывода: выносит вывод в отдельное окно, а при открытом окне —
    /// закрывает его и возвращает вывод в панель.
    /// </summary>
    private void OnDetach(object sender, RoutedEventArgs e)
    {
        if (OutputWindow.IsOpen)
        {
            OutputWindow.CloseIfOpen();
            return;
        }

        OutputWindow.Open(Window.GetWindow(this)!, Batch, ResultsGrid.SelectedItem as OperationResult);
    }

    /// <summary>Пока вывод в отдельном окне, на его месте в панели — подсказка про это окно.</summary>
    private void ApplyDetachState()
    {
        var detached = OutputWindow.IsOpen;

        OutputBox.Visibility = detached ? Visibility.Collapsed : Visibility.Visible;
        DetachedHint.Visibility = detached ? Visibility.Visible : Visibility.Collapsed;

        DetachIcon.Symbol = detached ? SymbolRegular.ArrowImport24 : SymbolRegular.WindowNew24;
        DetachButton.ToolTip = detached
            ? "Вернуть вывод в панель — отдельное окно закроется"
            : "Открыть вывод в отдельном окне";
    }

    /// <summary>Складывает вывод всех серверов в буфер обмена одним текстом.</summary>
    private void OnCopyAll(object sender, RoutedEventArgs e) => CopyAll(Batch);

    /// <summary>
    /// «Копировать. Все сервера»: вывод всех серверов одним текстом. Тем же зовёт
    /// галочка «после команды» / «после копирования», когда операция закончилась.
    /// </summary>
    /// <param name="finished">Автокопия: что закончилось — идёт в начало сообщения.</param>
    public void CopyAll(OperationBatch? source, string? finished = null)
    {
        if (source is not { Results.Count: > 0 } batch)
        {
            AppLog.Hint("Копировать нечего — результатов пока нет.", LogLevel.Warning);
            return;
        }

        var text = batch.BuildTranscript();

        // «+ команды»: следом вывод личных команд вкладки — один отчёт, сначала копирование
        if (AppState.Current.ChainFor(batch) is { } chain)
        {
            text = "═════ Копирование ═════" + Environment.NewLine + text + Environment.NewLine
                + OutputFeed.ChainHeader + Environment.NewLine + chain.BuildTranscript();
        }

        if (ClipboardText.CopyTerminalOutput(text, auto: finished is not null))
        {
            AppLog.Hint((finished is not null ? $"{finished} — вывод всех серверов скопирован в буфер обмена: " : "Вывод скопирован в буфер обмена: ")
                + $"серверов {batch.Results.Count}, строк {text.Split('\n').Length}.");
        }
    }

    /// <summary>
    /// Галочка «после …» у текущего окна. Срабатывает, где бы человек ни был — на другой
    /// вкладке, в другом разделе или вовсе не в программе: берётся сервер, выбранный в таблице
    /// этого прогона, а если его не выбирали — первый (при одиночном выборе он и единственный).
    /// </summary>
    public void CopyCurrentAfter(OperationBatch batch, string finished)
    {
        var result = ReferenceEquals(Batch, batch) && ResultsGrid.SelectedItem is OperationResult shown
            ? shown
            : batch.Picked is { } picked && batch.Results.Contains(picked)
                ? picked
                : batch.Results.FirstOrDefault();

        if (result is null)
        {
            AppLog.Hint($"{finished}, но результатов нет — копировать нечего.", LogLevel.Warning);
            return;
        }

        CopyResult(result, finished, batch);
    }

    /// <summary>Вывод только выбранного сервера.</summary>
    private void OnCopyOne(object sender, RoutedEventArgs e) => CopyCurrent();

    /// <summary>
    /// «Копировать. Текущее окно»: вывод сервера, выбранного в таблице слева.
    /// Тем же зовёт значок над полем команды — чтобы не тянуться мышью к терминалу.
    /// </summary>
    public void CopyCurrent(string? finished = null)
    {
        if (ResultsGrid.SelectedItem is not OperationResult result)
        {
            AppLog.Hint("Сначала выберите сервер в таблице слева.", LogLevel.Warning);
            return;
        }

        CopyResult(result, finished, Batch);
    }

    /// <summary>Вывод одного сервера в буфер — по кнопке или галочкой «после …».</summary>
    private static void CopyResult(OperationResult result, string? finished, OperationBatch? batch)
    {
        var text = result.Output.TrimEnd();

        if (text.Length == 0)
        {
            AppLog.Hint($"У сервера {result.Title} вывода пока нет.", LogLevel.Warning);
            return;
        }

        if (result.TimeLine is { } time)
        {
            text += Environment.NewLine + time;
        }

        // «+ команды»: следом вывод личных команд этого же сервера
        if (batch is not null
            && AppState.Current.ChainFor(batch) is { } chain
            && chain.Results.FirstOrDefault(r => ReferenceEquals(r.Server, result.Server)) is { } after
            && after.Output.TrimEnd() is { Length: > 0 } afterText)
        {
            text = "═════ Копирование ═════" + Environment.NewLine + text + Environment.NewLine + Environment.NewLine
                + OutputFeed.ChainHeader + Environment.NewLine + afterText
                + (after.TimeLine is { } afterTime ? Environment.NewLine + afterTime : string.Empty);
        }

        if (ClipboardText.CopyTerminalOutput(text + Environment.NewLine, auto: finished is not null))
        {
            AppLog.Hint(finished is not null
                ? $"{finished} — вывод скопирован в буфер обмена: {result.Title}."
                : $"Вывод скопирован: {result.Title}.");
        }
    }

    private async void OnRetry(object sender, RoutedEventArgs e)
    {
        if (Batch is { } batch)
        {
            await batch.RetryFailedAsync(AppState.Current.Settings.MaxParallel);

            if (AppState.Current.IsCommandBatch(batch) || AppState.Current.IsCopyBatch(batch))
            {
                AppState.Current.NotifyFinished(batch);
            }
        }
    }
}
