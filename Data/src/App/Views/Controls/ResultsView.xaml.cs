using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
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

        // работа пошла или кончилась, «1 из 2» стало длиннее — место под «Остановить» пересчитать
        RunTools.IsVisibleChanged += (_, _) => FitLeftTools();
        RunText.SizeChanged += (_, _) => FitLeftTools();

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
                foreach (var result in batch.AllRows)
                {
                    result.RaiseAgo();
                }
            }
        };
        Loaded += (_, _) => agoTimer.Start();
        Unloaded += (_, _) => agoTimer.Stop();

        // окно команды в «Проводнике»: открыто ли оно — помнится между запусками, команда пошла — назад к выводу
        _commandOpen = AppState.Current.Settings.ExplorerCommandOpen;
        CommandFace.Launched += () => Flip(false);
        AppState.Current.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppState.Section))
            {
                ApplyFace();
            }
        };
        ApplyFace();
    }

    private OperationBatch? Batch => DataContext as OperationBatch;

    // ── окно команды на обороте вывода («Проводник») ──────────────────────────

    /// <summary>Половина переворота: сжатие к середине или разворот другой стороной.</summary>
    private static readonly Duration FlipHalf = new(TimeSpan.FromMilliseconds(130));

    /// <summary>Вывод перевёрнут окном команды; видно это только в «Проводнике».</summary>
    private bool _commandOpen;

    private bool _flipping;

    private bool CommandShown => _commandOpen && AppState.Current.Section == AppSection.Explorer;

    private void OnFlip(object sender, RoutedEventArgs e) => Flip(!_commandOpen);

    /// <summary>Переворот как у страницы: окно сжимается к середине и разворачивается другой стороной.</summary>
    private void Flip(bool command)
    {
        if (_flipping || command == _commandOpen)
        {
            return;
        }

        _flipping = true;
        _commandOpen = command;
        AppState.Current.Settings.ExplorerCommandOpen = command;
        ApplyFlipButton();

        var fold = new DoubleAnimation(1, 0, FlipHalf) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseIn } };
        fold.Completed += (_, _) =>
        {
            ShowFace(command);

            var unfold = new DoubleAnimation(0, 1, FlipHalf) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut } };
            unfold.Completed += (_, _) =>
            {
                FlipScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                _flipping = false;

                if (command)
                {
                    CommandFace.FocusFirst();
                }
            };
            FlipScale.BeginAnimation(ScaleTransform.ScaleXProperty, unfold);
        };
        FlipScale.BeginAnimation(ScaleTransform.ScaleXProperty, fold);
    }

    /// <summary>Сторона без переворота — при запуске и смене раздела: окно команды есть только в «Проводнике».</summary>
    private void ApplyFace()
    {
        FlipScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _flipping = false;
        ShowFace(CommandShown);
        ApplyFlipButton();
    }

    private void ShowFace(bool command)
    {
        CommandFace.Visibility = command ? Visibility.Visible : Visibility.Collapsed;
        OutputFace.Visibility = command ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Открыто окно команды — белый значок на тускло-зелёной плашке.</summary>
    private void ApplyFlipButton()
    {
        if (_commandOpen)
        {
            FlipBadge.Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x3E));
            FlipGlyph.Foreground = Brushes.White;
            FlipText.Text = "Закрыть терминал";
            FlipButton.ToolTip = "Вернуть окно вывода";
        }
        else
        {
            FlipBadge.Background = Brushes.Transparent;
            FlipGlyph.ClearValue(IconElement.ForegroundProperty);
            FlipText.Text = "Открыть терминал";
            FlipButton.ToolTip = "Перевернуть окно вывода окном команды для сервера правой панели";
        }
    }

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
        var any = new Size(double.PositiveInfinity, double.PositiveInfinity);
        LeftTools.Measure(any);
        var need = LeftTools.DesiredSize.Width;

        // пока идёт работа — ещё «1 из 2» и «Остановить»: у узкой таблицы их срезало «Хранить вывод» справа
        if (RunTools.Visibility == Visibility.Visible)
        {
            RunTools.Measure(any);
            need += RunTools.DesiredSize.Width;
        }

        LeftToolsColumn.Width = new GridLength(Math.Max(need, TablePane.ActualWidth - _toolIndent));
    }

    /// <summary>Команды после копирования показанного прогона; null — их нет.</summary>
    private OperationBatch? _chain;

    /// <summary>Строки таблицы, когда у копирования есть команды после него: сервер, под ним его команды.</summary>
    private readonly ObservableCollection<OperationResult> _rows = new();

    /// <summary>Соседний прогон показанного: его строки идут в той же таблице.</summary>
    private OperationBatch? _companion;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is OperationBatch old)
        {
            old.Results.CollectionChanged -= OnResultsChanged;
        }

        if (_companion is not null)
        {
            _companion.Results.CollectionChanged -= OnResultsChanged;
            _companion = null;
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

            if (batch.Companion is { } companion)
            {
                _companion = companion;
                _companion.Results.CollectionChanged += OnResultsChanged;
            }

            // сводка приходит уже заполненной — события не будет, выбираем сами
            if (_rows.Count > 0)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ResultsGrid.SelectedItem is null && ReferenceEquals(DataContext, batch) && _rows.Count > 0)
                    {
                        ResultsGrid.SelectedItem = _rows[0];
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
        if (Batch is not { AllRows.Count: > 0 } batch)
        {
            AppLog.Hint("Список запусков и так пуст.");
            return;
        }

        var gone = batch.ClearFinished();

        AppLog.Hint(gone == 0
            ? "Убирать нечего — все запуски в списке ещё идут."
            : batch.AllRows.Count > 0
                ? $"Список очищен: убрано {gone}, идущие остались."
                : $"Список запусков очищен: убрано {gone}.");
    }

    /// <summary>
    /// Строки таблицы. Свежий запуск — сверху, строки внутри запуска — в порядке, как пошли
    /// (серверы — как в панели слева), между запусками черта. Копирование с командами после него:
    /// под строкой копирования каждого сервера — строки его команд, те же, что в разделе «Команда».
    /// Сам прогон строки не переставляет — на его порядок опираются копирование логов и цепочки.
    /// Команды мини-терминала идут тем же списком, что и копирование, каким бы ни был показанный прогон.
    /// </summary>
    private void ShowRows()
    {
        var chain = Batch is { } batch ? AppState.Current.AfterCopyBatchOf(batch) : null;
        var copying = Batch;

        // команды мини-терминала шли последними: показан их прогон, а строки копирования — со своими командами
        if (chain is null && Batch?.Companion is { } other && AppState.Current.AfterCopyBatchOf(other) is { } otherChain)
        {
            chain = otherChain;
            copying = other;
        }

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
        var runs = shown.AllRows
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

                if (following is null || copying is null || !ReferenceEquals(row.Batch, copying))
                {
                    continue;
                }

                if (AppState.AfterRowOf(copying, row, following) is { } after)
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

        // ответ в сессию «PowerShell» — новая строка: таблица переходит на неё, и её вывод сразу перед глазами.
        // Только если смотрели на ту же сессию: чужой выбор не сбивается
        if (_rows.Where(r => r.SessionOf is not null && !_seenTurns.Contains(r)).ToList() is { Count: > 0 } fresh)
        {
            foreach (var turn in fresh)
            {
                _seenTurns.Add(turn);
            }

            var newest = fresh[^1];

            if (picked is null || picked is OperationResult was && (ReferenceEquals(was, newest.SessionOf) || ReferenceEquals(was.SessionOf, newest.SessionOf)))
            {
                picked = newest;
            }
        }

        if (picked is not null && _rows.Contains(picked) && !ReferenceEquals(ResultsGrid.SelectedItem, picked))
        {
            ResultsGrid.SelectedItem = picked;
        }

        FitServerColumn();
    }

    /// <summary>Строки ответов в сессию, которые таблица уже видела, — на новую она переходит один раз.</summary>
    private readonly HashSet<OperationResult> _seenTurns = [];

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
    /// «Сервер» — по содержимому, ширина меряется здесь и ставится числом. Сброс «Auto» через ноль
    /// полагался на замер таблицы: если его не было (панель скрыта, строки ещё не построены),
    /// столбец оставался на 20 до первого изменения ширины окна.
    /// </summary>
    private void FitServerColumn() => Dispatcher.BeginInvoke(new Action(() =>
    {
        var summary = Batch?.IsSummary == true;
        var dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var mode = TextOptions.GetTextFormattingMode(ResultsGrid);

        double TextWidth(string text, FontFamily font, double size, FontWeight weight) =>
            new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(font, FontStyles.Normal, weight, FontStretches.Normal), size, Brushes.Black, null, mode, dip).WidthIncludingTrailingWhitespace;

        var cellFont = new FontFamily("Consolas");
        var cells = _rows
            .Select(r => summary ? r.ScopedShortTitle : r.ShortTitle)
            .Select(t => TextWidth(t, cellFont, 12, FontWeights.Normal))
            .DefaultIfEmpty(0)
            .Max();
        var head = TextWidth(ServerColumn.Header as string ?? string.Empty, ResultsGrid.FontFamily, 11, FontWeights.SemiBold);

        // 16 — поля ячейки, остальное — запас на рамку и округление до пикселей.
        // MinWidth вровень: иначе таблица при нехватке места ужмёт столбец и не отдаст обратно
        var width = Math.Min(ServerColumn.MaxWidth, Math.Ceiling(Math.Max(cells, head) + 22));

        ServerColumn.MinWidth = width;
        ServerColumn.Width = new DataGridLength(width);
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
        OutputCountdown.Visibility = OutputBox.Visibility;
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
        if (source is not { AllRows.Count: > 0 } batch)
        {
            AppLog.Hint("Копировать нечего — результатов пока нет.", LogLevel.Warning);
            return;
        }

        var chain = AppState.Current.ChainFor(batch);
        var tab = AppState.Current.TabOfBatch(batch);

        // автокопия — только строки запусков: «убрана строка», «создана папка» в чат не идут
        var runsOnly = finished is not null;
        var level = FinishedLevel(finished is not null && batch.RunSucceeded && chain?.RunSucceeded != false);

        // «+ команды»: следом вывод личных команд вкладки — один отчёт, сначала копирование
        string Build(int? share) => chain is null
            ? batch.BuildTranscript(share, runsOnly)
            : OutputFeed.CopyHeader + Environment.NewLine + batch.BuildTranscript(share, runsOnly) + Environment.NewLine
                + OutputFeed.ChainHeader + Environment.NewLine + chain.BuildTranscript(share, runsOnly);

        var text = Build(null);
        string? cut = null;

        // лог длиннее LogTrim.AllServers строк подвешивал программу, куда его вставляли (и CopyPass):
        // с «*.txt» — файлом с полным логом, и автокопией, и кнопкой. Без неё автокопия — обрезкой,
        // всем серверам поровну, кнопка — целиком
        if (LogTrim.Count(text) > LogTrim.AllServers && (finished is not null || tab.LogAsFile))
        {
            if (tab.LogAsFile)
            {
                var lines = LogTrim.Count(text);
                var name = batch.Results.FirstOrDefault()?.Scope is { Length: > 0 } scope ? scope : "все серверы";

                if (ClipboardText.CopyTerminalOutputFile(text, name, tab, auto: finished is not null) is { } file)
                {
                    AppLog.Hint((finished ?? "Вывод скопирован") + $" — лог длинный ({lines:N0} строк), в буфере файлом: {file}.", level);
                }

                return;
            }

            var whole = text;
            text = Build(LogTrim.ShareOf(batch.Results.Count + (chain?.Results.Count ?? 0)));
            cut = CutNote(whole, text);
        }

        if (ClipboardText.CopyTerminalOutput(text, auto: finished is not null, tab))
        {
            // автокопия целиком — молча: об окончании уже сказал итог, сбой буфера скажет сам
            if (cut is not null)
            {
                AppLog.Hint(cut, level);
            }
            else if (finished is null)
            {
                AppLog.Echo($"Вывод скопирован в буфер обмена: запусков {batch.AllRows.Count}, строк {text.Split('\n').Length}.");
            }
        }
    }

    /// <summary>Автокопия после удачного запуска — плашка зелёная. Кнопка и неудачный запуск — обычная.</summary>
    private static LogLevel FinishedLevel(bool succeeded) => succeeded ? LogLevel.Success : LogLevel.Info;

    /// <summary>Плашка об обрезанной автокопии; null — обрезать оказалось нечего.</summary>
    private static string? CutNote(string whole, string sent) => whole == sent
        ? null
        : $"В буфере обрезанный лог: {LogTrim.Count(sent):N0} строк из {LogTrim.Count(whole):N0}.";

    /// <summary>
    /// Галочка «после …» у текущего окна. Срабатывает, где бы человек ни был — на другой
    /// вкладке, в другом разделе или вовсе не в программе: берётся сервер, выбранный в таблице
    /// этого прогона, а если его не выбирали — первый (при одиночном выборе он и единственный).
    /// </summary>
    public void CopyCurrentAfter(OperationBatch batch, string finished)
    {
        // строки разовых действий и прошлых запусков — не в счёт: автокопия про запуск, что закончился
        // строка ответа в сессию «PowerShell» — часть запуска самой сессии
        bool Usable(OperationResult row) => !row.IsAction
            && (batch.LastRun.Count == 0 || batch.LastRun.Any(r => r.RunId == (row.SessionOf ?? row.Parent ?? row).RunId));

        var result = ReferenceEquals(Batch, batch) && ResultsGrid.SelectedItem is OperationResult shown && Usable(shown)
            ? shown
            : batch.Picked is { } picked && batch.Results.Contains(picked) && Usable(picked)
                ? picked
                : batch.LastRun.FirstOrDefault() ?? batch.Results.FirstOrDefault(r => !r.IsAction);

        if (result is null)
        {
            AppLog.Hint($"{finished}, но результатов нет — копировать нечего.", LogLevel.Warning);
            return;
        }

        CopyResult(result, finished, batch);
    }

    /// <summary>«Авто» для ответа в сессию «PowerShell»: только этот ответ и его вывод.</summary>
    public void CopyTurn(OperationResult turn)
    {
        AppLog.Debug($"PowerShell: «авто» — вывод ответа в буфер, {turn.Output.Length} символов.");
        CopyResult(turn, "Ответ пришёл", turn.Batch, turnOnly: true);
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
    /// <param name="turnOnly">Ответ в сессию — только он, а не вся сессия.</param>
    private static void CopyResult(OperationResult result, string? finished, OperationBatch? batch, bool turnOnly = false)
    {
        if (result.Output.TrimEnd().Length == 0)
        {
            AppLog.Hint($"У сервера {result.Title} вывода пока нет.", LogLevel.Warning);
            return;
        }

        var auto = finished is not null;

        // сессия «PowerShell»: каждый ответ — своей строкой; автокопия по её окончании берёт всю сессию —
        // запуск и все ответы с их выводом. Кнопка на строке ответа — только этот ответ
        var session = result.SessionOf ?? result;
        var turns = batch?.AllRows.Where(r => ReferenceEquals(r.SessionOf, session)).ToList() ?? [];

        if (auto && turns.Count > 0 && !turnOnly)
        {
            result = session;
        }

        var wholeSession = turns.Count > 0 && ReferenceEquals(result, session);
        var tab = batch is null ? null : AppState.Current.TabOfBatch(batch);
        var after = batch is not null && AppState.Current.ChainFor(batch) is { } chain
            ? AppState.AfterRowOf(batch, result, chain)
            : null;
        var level = FinishedLevel(auto && result.Status == OperationStatus.Success && after?.Status is null or OperationStatus.Success);

        // в буфер — то же, что показано в выводе: шапка с сервером, разделом и вкладкой,
        // «+ команды» — следом вывод личных команд этого же сервера
        string Build(int? max) => wholeSession
            ?string.Join(Environment.NewLine + Environment.NewLine,
                new[] { session }.Concat(turns).Select(r => OutputFeed.Compose(r, null, max, "Текущий лог").TrimEnd()))
            : OutputFeed.Compose(result, after, max, "Текущий лог").TrimEnd();

        var full = Build(null);
        string? cut = null;

        // длиннее LogTrim.PerServer строк: с «*.txt» — файлом с полным логом, и автокопией, и кнопкой;
        // без неё автокопия — обрезкой, кнопка — целиком
        if (LogTrim.Count(full) > LogTrim.PerServer && (auto || tab is { LogAsFile: true }))
        {
            if (tab is { LogAsFile: true })
            {
                if (ClipboardText.CopyTerminalOutputFile(full + Environment.NewLine, result.Server.PaneLabel, tab, auto) is { } file)
                {
                    AppLog.Hint((finished ?? "Вывод скопирован") + $" — лог длинный ({LogTrim.Count(full):N0} строк), в буфере файлом: {file}.", level);
                }

                return;
            }

            var whole = full;
            full = Build(LogTrim.PerServer);
            cut = CutNote(whole, full);
        }

        if (ClipboardText.CopyTerminalOutput(full + Environment.NewLine, auto: auto, tab))
        {
            // автокопия целиком — молча: об окончании уже сказал итог, сбой буфера скажет сам
            if (cut is not null)
            {
                AppLog.Hint(cut, level);
            }
            else if (!auto)
            {
                AppLog.Echo($"Вывод скопирован: {result.Title}.");
            }
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
