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
    /// Строки таблицы. Свежий запуск — сверху, строки внутри запуска — в порядке, как пошли
    /// (серверы — как в панели слева), между запусками черта. Копирование с командами после него:
    /// под строкой копирования каждого сервера — строки его команд, те же, что в разделе «Команда».
    /// Сам прогон строки не переставляет — на его порядок опираются копирование логов и цепочки.
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

        var picked = ResultsGrid.SelectedItem;
        var following = _chain;
        var rows = new List<OperationResult>();
        var watched = new HashSet<OperationResult>();

        // запуски — по номеру; свежий тот, чья строка добавлена позже всех. Строки старых версий без
        // номера — каждая сама по себе
        var runs = shown.Results
            .Select((row, index) => (Row: row, Index: index))
            .GroupBy(x => x.Row.RunId != 0 ? x.Row.RunId : -(x.Index + 1))
            .OrderByDescending(g => g.Max(x => x.Index));

        foreach (var run in runs)
        {
            var first = true;

            foreach (var (row, _) in run.OrderBy(x => x.Index))
            {
                // черта — над первой строкой каждого запуска, кроме самого верхнего
                row.StartsRun = first && rows.Count > 0;
                first = false;
                rows.Add(row);

                if (following is null)
                {
                    continue;
                }

                if (AppState.AfterRowOf(shown, row, following) is { } after)
                {
                    watched.Add(after);

                    // команды — каждая своей строкой: копирование, #1, #2…; пока ни одна не пошла — общая строка
                    var steps = after.Steps;

                    if (steps.Count == 0)
                    {
                        after.StartsRun = false;
                        rows.Add(after);
                    }
                    else
                    {
                        foreach (var step in steps)
                        {
                            step.StartsRun = false;
                            rows.Add(step);
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
                        step.StartsRun = false;
                        rows.Add(step);
                    }
                }
            }
        }

        WatchSteps(watched);

        // те же строки в том же порядке — таблицу не дёргаем: выделение и прокрутка на месте
        if (!rows.SequenceEqual(_rows))
        {
            _rows.Clear();

            foreach (var row in rows)
            {
                _rows.Add(row);
            }
        }

        if (!ReferenceEquals(ResultsGrid.ItemsSource, _rows))
        {
            ResultsGrid.ItemsSource = _rows;
        }

        if (picked is not null && _rows.Contains(picked) && !ReferenceEquals(ResultsGrid.SelectedItem, picked))
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

        // верхняя строка — самая свежая
        if (ResultsGrid.SelectedItem is null && _rows.Count > 0)
        {
            ResultsGrid.SelectedItem = _rows[0];
        }

        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            FollowNewRun();
        }

        FitServerColumn();
    }

    private bool _following;

    /// <summary>
    /// Свежий запуск встаёт сверху — терминал сам идёт за ним: его первая строка выбирается
    /// (справа её вывод), таблица прокручивается к ней. Строки запуска добавляются по одной —
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

            // свежее — верхняя строка таблицы: запуск или разовое действие, что добавлено последним
            if (_rows.Count == 0)
            {
                return;
            }

            ResultsGrid.SelectedItem = _rows[0];
            ResultsGrid.ScrollIntoView(_rows[0]);
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

        var chain = AppState.Current.ChainFor(batch);
        var tab = AppState.Current.TabOfBatch(batch);

        // автокопия — только строки запусков: «убрана строка», «создана папка» в чат не идут
        var runsOnly = finished is not null;

        // «+ команды»: следом вывод личных команд вкладки — один отчёт, сначала копирование
        string Build(int? share) => chain is null
            ? batch.BuildTranscript(share, runsOnly)
            : "═════ Копирование ═════" + Environment.NewLine + batch.BuildTranscript(share, runsOnly) + Environment.NewLine
                + OutputFeed.ChainHeader + Environment.NewLine + chain.BuildTranscript(share, runsOnly);

        var text = Build(null);

        // автокопия длиннее LogTrim.AllServers строк подвешивала программу, куда её вставляли (и CopyPass):
        // с «*.txt» — файлом с полным логом, без неё — обрезкой, всем серверам поровну. Кнопка — целиком
        if (finished is not null && LogTrim.Count(text) > LogTrim.AllServers)
        {
            if (tab.LogAsFile)
            {
                var lines = LogTrim.Count(text);
                var name = batch.Results.FirstOrDefault()?.Scope is { Length: > 0 } scope ? scope : "все серверы";

                if (ClipboardText.CopyTerminalOutputFile(text, name, tab) is { } file)
                {
                    AppLog.Hint($"{finished} — лог длинный ({lines:N0} строк), в буфере файлом: {file}.");
                }

                return;
            }

            text = Build(LogTrim.ShareOf(batch.Results.Count + (chain?.Results.Count ?? 0)));
        }

        if (ClipboardText.CopyTerminalOutput(text, auto: finished is not null, tab))
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
        // строки разовых действий — не в счёт: автокопия про запуск
        var result = ReferenceEquals(Batch, batch) && ResultsGrid.SelectedItem is OperationResult { IsAction: false } shown
            ? shown
            : batch.Picked is { IsAction: false } picked && batch.Results.Contains(picked)
                ? picked
                : batch.LastRun.FirstOrDefault() ?? batch.Results.FirstOrDefault(r => !r.IsAction);

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
        if (result.Output.TrimEnd().Length == 0)
        {
            AppLog.Hint($"У сервера {result.Title} вывода пока нет.", LogLevel.Warning);
            return;
        }

        var auto = finished is not null;
        var tab = batch is null ? null : AppState.Current.TabOfBatch(batch);
        var after = batch is not null && AppState.Current.ChainFor(batch) is { } chain
            ? AppState.AfterRowOf(batch, result, chain)
            : null;

        // имя и адрес сервера сверху — иначе в буфере не понять, к чему лог;
        // «+ команды» — следом вывод личных команд этого же сервера
        string Build(int? max)
        {
            var output = result.Output.TrimEnd();
            output = max is { } m ? LogTrim.Lines(output, m, "Текущий лог") : output;

            var text = result.CopyHeader(batch?.IsSummary == true) + Environment.NewLine + output
                + (result.TimeLine is { } time ? Environment.NewLine + time : string.Empty);

            if (after?.Output.TrimEnd() is { Length: > 0 } afterText)
            {
                afterText = max is { } am ? LogTrim.Lines(afterText, am, "Текущий лог") : afterText;
                text = "═════ Копирование ═════" + Environment.NewLine + text + Environment.NewLine + Environment.NewLine
                    + OutputFeed.ChainHeader + Environment.NewLine + afterText
                    + (after.TimeLine is { } afterTime ? Environment.NewLine + afterTime : string.Empty);
            }

            return text;
        }

        var full = Build(null);

        // автокопия длиннее LogTrim.PerServer строк: с «*.txt» — файлом с полным логом, без неё — обрезкой
        if (auto && LogTrim.Count(full) > LogTrim.PerServer)
        {
            if (tab is { LogAsFile: true })
            {
                if (ClipboardText.CopyTerminalOutputFile(full + Environment.NewLine, result.Server.PaneLabel, tab) is { } file)
                {
                    AppLog.Hint($"{finished} — лог длинный ({LogTrim.Count(full):N0} строк), в буфере файлом: {file}.");
                }

                return;
            }

            full = Build(LogTrim.PerServer);
        }

        if (ClipboardText.CopyTerminalOutput(full + Environment.NewLine, auto: auto, tab))
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
