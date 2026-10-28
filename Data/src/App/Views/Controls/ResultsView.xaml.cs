using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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

        // без сортировки: строка встаёт туда, где появилась, и там остаётся — запуски идут
        // сверху вниз по времени, команды после копирования — под строкой своего копирования

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

    /// <summary>Стоп в строке — только она; строка команды цепочки останавливает свой сервер в цепочке.</summary>
    private async void OnRowStop(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: OperationResult row })
        {
            await row.StopAsync();
        }
    }

    /// <summary>
    /// Строка кнопок стоит в одной строке с ярлыками «Терминал / Журнал»: слева отступ
    /// на их ширину, высота — как у них, чтобы кнопки встали по их середине.
    /// </summary>
    public void AlignToolbar(double indent, double height)
    {
        ToolBar.Margin = new Thickness(indent, 0, 0, 4);
        ToolBar.MinHeight = height;
        _toolIndent = indent;
        FitLeftTools();
    }

    private double _toolIndent;

    private void OnTablePaneSized(object sender, SizeChangedEventArgs e) => FitLeftTools();

    /// <summary>
    /// «Хранить лог» и «Сохранять лог» стоят у правой границы таблицы слева: левая часть строки
    /// кнопок — ровно до неё (строка начинается правее ярлыков «Терминал / Журнал»).
    /// </summary>
    private void FitLeftTools()
    {
        LeftTools.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        LeftToolsColumn.Width = new GridLength(Math.Max(LeftTools.DesiredSize.Width, TablePane.ActualWidth - _toolIndent));
    }

    /// <summary>Команды после копирования показанного прогона; null — их нет.</summary>
    private OperationBatch? _chain;

    /// <summary>Строки таблицы, когда у копирования есть команды после него: сервер, под ним его команды.</summary>
    private readonly ObservableCollection<OperationResult> _rows = new();

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is OperationBatch old)
        {
            old.Results.CollectionChanged -= OnResultsChanged;
        }

        ShowRows();
        ApplyTitles(e.NewValue as OperationBatch);

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

    private const string NoPickTitle = "Вывод — выберите сервер слева";

    /// <summary>
    /// Название вкладки у сервера — только в сводке «Выполнить везде», где в одной таблице строки
    /// разных вкладок. В терминале своей вкладки оно лишнее: какая вкладка, и так видно.
    /// </summary>
    private void ApplyTitles(OperationBatch? batch)
    {
        var summary = batch?.IsSummary == true;

        ServerColumn.Binding = new Binding(summary ? nameof(OperationResult.ScopedShortTitle) : nameof(OperationResult.ShortTitle));

        OutputTitle.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new Binding("SelectedItem." + (summary ? nameof(OperationResult.ScopedFullTitle) : nameof(OperationResult.FullTitle)))
        {
            ElementName = nameof(ResultsGrid),
            FallbackValue = NoPickTitle,
            TargetNullValue = NoPickTitle,
        });
    }

    /// <summary>Корзина в шапке таблицы: убирает законченные запуски из списка.</summary>
    private void OnClearRuns(object sender, RoutedEventArgs e)
    {
        if (Batch is not { HasResults: true } batch)
        {
            AppLog.Hint("Список запусков и так пуст.");
            return;
        }

        var gone = batch.ClearFinished();

        AppLog.Hint(gone == 0
            ? "Убирать нечего — все запуски в списке ещё идут."
            : batch.HasResults
                ? $"Список очищен: убрано {gone}, идущие остались."
                : $"Список запусков очищен: убрано {gone}.");
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
            WatchSteps([]);
            ResultsGrid.ItemsSource = null;
            return;
        }

        if (_chain is null)
        {
            WatchSteps([]);

            if (!ReferenceEquals(ResultsGrid.ItemsSource, shown.Results))
            {
                ResultsGrid.ItemsSource = shown.Results;
            }

            return;
        }

        var picked = ResultsGrid.SelectedItem;

        // проверено выше: без команд после копирования сюда не доходит
        OperationBatch following = _chain;

        _rows.Clear();

        var watched = new HashSet<OperationResult>();

        foreach (var row in shown.Results)
        {
            _rows.Add(row);

            if (AppState.AfterRowOf(shown, row, following) is { } after)
            {
                watched.Add(after);

                // команды — каждая своей строкой: копирование, #1, #2…; пока ни одна не пошла — общая строка
                var steps = after.Steps;

                if (steps.Count == 0)
                {
                    _rows.Add(after);
                }
                else
                {
                    foreach (var step in steps)
                    {
                        _rows.Add(step);
                    }
                }
            }
            else
            {
                // цепочка ещё не пошла — её команды стоят строками «ждёт копирования» с самого старта
                // копирования; строка копирования получает их сразу после появления — следим за ней
                watched.Add(row);

                foreach (var step in row.Steps)
                {
                    _rows.Add(step);
                }
            }
        }

        WatchSteps(watched);

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

    /// <summary>Строки команд, у которых таблица ждёт появления следующей команды.</summary>
    private readonly HashSet<OperationResult> _stepsWatched = [];

    private void WatchSteps(HashSet<OperationResult> rows)
    {
        foreach (var gone in _stepsWatched.Where(r => !rows.Contains(r)).ToList())
        {
            gone.StepsChanged -= OnStepsChanged;
            _stepsWatched.Remove(gone);
        }

        foreach (var row in rows.Where(_stepsWatched.Add))
        {
            row.StepsChanged += OnStepsChanged;
        }
    }

    // команда пошла на сервере — в фоне; таблицу перестраивает окно
    private void OnStepsChanged(OperationResult row) => Dispatcher.BeginInvoke(new Action(ShowRows));

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

        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            FollowNewRun();
        }

        FitServerColumn();
    }

    private bool _following;

    /// <summary>
    /// Прогоны копятся сверху вниз — терминал сам идёт за новым: его первая строка выбирается
    /// (справа её вывод), строки встают в видимую часть. Строки прогона добавляются по одной —
    /// переход один, после последней.
    /// </summary>
    private void FollowNewRun()
    {
        if (_following)
        {
            return;
        }

        _following = true;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            _following = false;

            if (Batch is not { LastRun: { Count: > 0 } run } || !ResultsGrid.Items.Contains(run[0]))
            {
                return;
            }

            ResultsGrid.SelectedItem = run[0];

            // сначала последняя, потом первая: видна первая строка прогона и сколько влезет следом
            ResultsGrid.ScrollIntoView(run[^1]);
            ResultsGrid.ScrollIntoView(run[0]);
        }), System.Windows.Threading.DispatcherPriority.Background);
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

    /// <summary>Окно, в которое вынесена таблица запусков; null — таблица на месте.</summary>
    private RunsWindow? _runsWindow;

    /// <summary>
    /// Кнопка в шапке таблицы: переносит таблицу в отдельное окно, а при открытом окне —
    /// закрывает его, и таблица возвращается на место.
    /// </summary>
    private void OnDetachTable(object sender, RoutedEventArgs e)
    {
        if (_runsWindow is not null)
        {
            _runsWindow.Close();
            return;
        }

        DetachTable();
        AppLog.Hint("Список запусков открыт в отдельном окне.");
    }

    private void DetachTable()
    {
        TableHost.Children.Remove(ResultsGrid);

        var window = new RunsWindow(ResultsGrid) { Owner = Window.GetWindow(this) };
        _runsWindow = window;

        window.Closed += (_, _) =>
        {
            _runsWindow = null;
            TableHost.Children.Insert(0, ResultsGrid);
            ApplyTableDetach();

            // закрылось при выходе из программы — при следующем запуске откроется снова
            if (!App.Quitting)
            {
                AppState.Current.Settings.RunsDetached = false;
                AppLog.Hint("Окно запусков закрыто — список вернулся в главное окно.");
            }
        };

        window.Show();
        ApplyTableDetach();
        AppState.Current.Settings.RunsDetached = true;
    }

    /// <summary>При запуске программы: окна вывода и запусков, вынесенные в прошлый раз, — снова отдельно.</summary>
    public void RestoreDetached(bool output, bool runs)
    {
        if (runs && _runsWindow is null)
        {
            DetachTable();
        }

        if (output && !OutputWindow.IsOpen)
        {
            OutputWindow.Open(Window.GetWindow(this)!, Batch, ResultsGrid.SelectedItem as OperationResult);
        }
    }

    private void ApplyTableDetach()
    {
        var detached = _runsWindow is not null;

        TableDetachedHint.Visibility = detached ? Visibility.Visible : Visibility.Collapsed;
        TableDetachIcon.Symbol = detached ? SymbolRegular.ArrowImport24 : SymbolRegular.WindowNew24;
        TableDetachButton.ToolTip = detached
            ? "Вернуть список запусков в главное окно — отдельное окно закроется"
            : "Открыть список запусков в отдельном окне";
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
    /// «Все логи»: вывод всех серверов одним текстом. Тем же зовёт
    /// галочка «авто», когда операция закончилась.
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

        if (ClipboardText.CopyTerminalOutput(text, auto: finished is not null, AppState.Current.SectionOfBatch(batch)))
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
    /// «Текущий лог»: вывод сервера, выбранного в таблице слева.
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

        // имя и адрес сервера сверху — иначе в буфере не понять, к чему лог
        text = result.CopyHeader(batch?.IsSummary == true) + Environment.NewLine + text;

        // «+ команды»: следом вывод личных команд этого же сервера
        if (batch is not null
            && AppState.Current.ChainFor(batch) is { } chain
            && AppState.AfterRowOf(batch, result, chain) is { } after
            && after.Output.TrimEnd() is { Length: > 0 } afterText)
        {
            text = "═════ Копирование ═════" + Environment.NewLine + text + Environment.NewLine + Environment.NewLine
                + OutputFeed.ChainHeader + Environment.NewLine + afterText
                + (after.TimeLine is { } afterTime ? Environment.NewLine + afterTime : string.Empty);
        }

        if (ClipboardText.CopyTerminalOutput(text + Environment.NewLine, auto: finished is not null,
            batch is null ? null : AppState.Current.SectionOfBatch(batch)))
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
