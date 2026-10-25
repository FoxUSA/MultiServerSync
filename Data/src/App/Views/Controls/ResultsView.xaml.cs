using System.Collections.Specialized;
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

        // порядок в таблице выбирается щелчком по заголовку и не сбрасывается
        GridSort.Keep(ResultsGrid, "results");

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

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is OperationBatch old)
        {
            old.Results.CollectionChanged -= OnResultsChanged;
        }

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

    /// <summary>Первый сервер выделяется сам, чтобы панель вывода не пустовала.</summary>
    private void OnResultsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
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
            AppLog.Warn("Сначала выберите сервер в таблице слева — очищается вывод выбранного.");
            return;
        }

        if (result.Output.Length == 0)
        {
            AppLog.Info($"У сервера {result.Title} вывода и так нет.");
            return;
        }

        result.ClearOutput();
        AppLog.Info($"Вывод очищен: {result.Title}.");
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
            AppLog.Warn("Копировать нечего — результатов пока нет.");
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
            AppLog.Success((finished is not null ? $"{finished} — вывод всех серверов скопирован в буфер обмена: " : "Вывод скопирован в буфер обмена: ")
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
            AppLog.Warn($"{finished}, но результатов нет — копировать нечего.");
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
            AppLog.Warn("Сначала выберите сервер в таблице слева.");
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
            AppLog.Warn($"У сервера {result.Title} вывода пока нет.");
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
            AppLog.Success(finished is not null
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
