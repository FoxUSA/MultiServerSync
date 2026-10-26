using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using MultiServerSync.Core;
using MultiServerSync.Models;

namespace MultiServerSync.Services;

public delegate Task ServerWork(ServerEntry server, OperationResult result, CancellationToken ct);

/// <summary>
/// Один прогон операции по всем отмеченным серверам: очередь, параллельность,
/// прогресс, итоговая сводка «сколько приняли успешно» и повтор по упавшим.
/// </summary>
public sealed class OperationBatch : ObservableObject
{
    private CancellationTokenSource? _cts;
    private ServerWork? _lastWork;
    private Action<IReadOnlyList<ServerEntry>>? _beforeRetry;
    private Action? _afterRetry;
    private string _operationName = string.Empty;

    /// <summary>Прогоны других вкладок, если этот прогон — сводный.</summary>
    private IReadOnlyList<OperationBatch> _sources = [];

    private DateTime? _finishedAt;

    private bool _isRunning;
    private int _total;
    private int _done;
    private int _success;
    private int _failed;

    public OperationBatch()
    {
        Results.CollectionChanged += (_, _) => Raise(nameof(HasResults));
    }

    public ObservableCollection<OperationResult> Results { get; } = new();

    /// <summary>Название вкладки, из которой запущена операция — попадает в строки результатов.</summary>
    public string? Scope { get; set; }

    /// <summary>Раздел вкладки — им помечаются записи журнала об этой операции.</summary>
    public AppSection? Section { get; set; }

    /// <summary>
    /// Команда шла через sudo: её процессы принадлежат root, и kill к ним тоже нужен через sudo.
    /// </summary>
    public bool UsedSudo { get; set; }

    public bool HasResults => Results.Count > 0;

    /// <summary>Когда начался последний прогон: команды после копирования показываются, только если они новее его.</summary>
    public DateTime? StartedAt { get; private set; }

    /// <summary>
    /// Сервер, выбранный в таблице терминала, пока он показывал этот прогон. По нему автокопия
    /// «Текущее окно» знает, чей вывод брать, даже когда терминал уже показывает другую вкладку.
    /// </summary>
    public OperationResult? Picked { get; set; }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (Set(ref _isRunning, value))
            {
                RaiseMany(nameof(IsIdle), nameof(CanRetry), nameof(SummaryText));
            }
        }
    }

    public bool IsIdle => !IsRunning;

    public int Total
    {
        get => _total;
        private set { if (Set(ref _total, value)) { RaiseMany(nameof(ProgressText), nameof(ProgressPercent), nameof(SummaryText)); } }
    }

    public int Done
    {
        get => _done;
        private set { if (Set(ref _done, value)) { RaiseMany(nameof(ProgressText), nameof(ProgressPercent), nameof(SummaryText)); } }
    }

    public int SuccessCount
    {
        get => _success;
        private set { if (Set(ref _success, value)) { Raise(nameof(SummaryText)); } }
    }

    public int FailedCount
    {
        get => _failed;
        private set { if (Set(ref _failed, value)) { RaiseMany(nameof(CanRetry), nameof(SummaryText)); } }
    }

    public bool CanRetry => !IsRunning && FailedCount > 0;

    public double ProgressPercent => Total == 0 ? 0 : Done * 100.0 / Total;

    public string ProgressText => Total == 0 ? string.Empty : $"{Done} из {Total}";

    /// <summary>
    /// Состояние прогона словами: пока идёт — сколько серверов прошло, после — время
    /// окончания и итог. По одному выводу не видно, закончилась команда или ещё идёт,
    /// поэтому эта строка стоит в окне вывода.
    /// </summary>
    public string SummaryText
    {
        get
        {
            if (IsRunning)
            {
                return $"выполняется: {Done} из {Total}";
            }

            if (Total == 0)
            {
                return "ещё не запускалось";
            }

            var finished = _finishedAt is { } time ? $"готово {time:HH:mm:ss} · " : string.Empty;
            var counts = $"успешно {SuccessCount} из {Total}";

            return FailedCount == 0
                ? finished + counts
                : $"{finished}{counts}, ошибок {FailedCount}";
        }
    }

    /// <summary>
    /// Весь вывод одним текстом — по одному блоку на сервер, как в терминале:
    /// «Сервер: Имя (адрес):», под ним строка команды и её ответ.
    /// </summary>
    public string BuildTranscript()
    {
        var text = new StringBuilder();

        foreach (var result in Results)
        {
            var output = result.Output.TrimEnd();
            var caption = result.Scope.Length == 0
                ? result.Server.Caption
                : $"{result.Scope} · {result.Server.Caption}";

            text.Append("Сервер: ").Append(caption).AppendLine(":");

            if (output.Length > 0)
            {
                text.AppendLine(output);

                // то же время, что в таблице слева: без вывода оно уже есть в строке статуса
                if (result.TimeLine is { } time)
                {
                    text.AppendLine(time);
                }
            }
            else
            {
                text.AppendLine($"(вывода нет · {result.StatusText})");
            }

            text.AppendLine();
        }

        return text.ToString().TrimEnd() + Environment.NewLine;
    }

    private bool _stopping;

    /// <summary>
    /// «Остановить» — одна кнопка на всё, её зовут все кнопки стопа программы. Команде на сервере —
    /// сначала Ctrl+C (сигнал INT), чтобы свернулась сама; не отпустила за 3 с — KILL;
    /// и после этого ещё 2 с — закрываем канал. Копирование и скачивание процесса на сервере
    /// не держат — их обрывает сразу закрытие канала. Второе нажатие не ждёт: KILL и закрыть.
    /// </summary>
    public async Task StopAsync()
    {
        // сводный прогон ничего не выполняет сам — останавливают те, кто выполняет
        if (_sources.Count > 0)
        {
            await Task.WhenAll(_sources.Select(s => s.StopAsync()));
            return;
        }

        if (!IsRunning)
        {
            return;
        }

        var hasProcess = Results.Any(r => r.IsRunning && r.RemoteGroup > 0);

        if (_stopping || !hasProcess)
        {
            if (hasProcess)
            {
                await SignalAsync("KILL", "снятие (сигнал KILL)");
            }

            Cancel();
            return;
        }

        _stopping = true;

        try
        {
            await SignalAsync("INT", "Ctrl+C (сигнал INT)");

            if (await StillRunningAfter(TimeSpan.FromSeconds(3)))
            {
                await SignalAsync("KILL", "снятие (сигнал KILL)");

                if (await StillRunningAfter(TimeSpan.FromSeconds(2)))
                {
                    Cancel();
                }
            }
        }
        finally
        {
            _stopping = false;
        }
    }

    private async Task<bool> StillRunningAfter(TimeSpan wait)
    {
        var until = DateTime.UtcNow + wait;

        while (IsRunning && DateTime.UtcNow < until)
        {
            await Task.Delay(100);
        }

        return IsRunning;
    }

    /// <summary>
    /// Посылает сигнал команде на тех серверах, где она ещё идёт. Отдельным подключением:
    /// канал команды занят её выводом, а «нажать Ctrl+C» в него нельзя — стандартный ввод
    /// закрыт сразу после передачи скрипта.
    /// </summary>
    private async Task SignalAsync(string signal, string what)
    {
        // сводный прогон ничего не выполняет сам — прерывают те, кто выполняет
        if (_sources.Count > 0)
        {
            await Task.WhenAll(_sources.Select(s => s.SignalAsync(signal, what)));
            return;
        }

        var targets = Results.Where(r => r.IsRunning && r.RemoteGroup > 0).ToList();

        if (targets.Count == 0)
        {
            AppLog.Hint(Results.Any(r => r.IsRunning)
                ? "Сервер ещё не сообщил, каким процессом идёт команда — повторите через секунду."
                : "Прерывать нечего: команда сейчас не выполняется.",
                LogLevel.Warning);
            return;
        }

        if (Section is { } section)
        {
            AppLog.Section = section;
        }

        AppLog.Info($"{what} — на {targets.Count} сервере(ах).");

        var settings = AppState.Current.Settings;

        await Task.WhenAll(targets.Select(async result =>
        {
            result.Interrupted = true;
            result.Append($"— послан {what} —");

            try
            {
                var sent = await SshService.SignalJobAsync(
                    result.Server, settings, result.RemotePid, result.RemoteGroup, signal, UsedSudo);

                if (sent)
                {
                    AppLog.Success($"{what} доставлен", result.Server.Display);
                }
                else
                {
                    AppLog.Warn($"{what} не доставлен: процесса уже нет или не хватило прав",
                        result.Server.Display);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error($"{what} не отправлен: " + SshService.Describe(ex, result.Server),
                    result.Server.Display);
            }
        }));
    }

    private void Cancel()
    {
        if (_sources.Count > 0)
        {
            foreach (var source in _sources)
            {
                source.Cancel();
            }

            return;
        }

        if (_cts is { IsCancellationRequested: false })
        {
            AppLog.Warn("Отмена операции по запросу пользователя.");
            _cts.Cancel();
        }
    }

    /// <summary>
    /// Сводный прогон: показывает результаты сразу нескольких вкладок одной таблицей.
    /// Строки — те же объекты, что и во вкладках, поэтому статусы обновляются сами.
    /// </summary>
    public void TrackAll(IReadOnlyList<OperationBatch> sources, string operationName)
    {
        foreach (var source in _sources)
        {
            source.PropertyChanged -= OnSourceChanged;
            source.Results.CollectionChanged -= OnSourceResultsChanged;
        }

        _sources = sources;
        _operationName = operationName;

        foreach (var source in _sources)
        {
            source.PropertyChanged += OnSourceChanged;
            source.Results.CollectionChanged += OnSourceResultsChanged;
        }

        RebuildFromSources();
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e) => Recount();

    private void OnSourceResultsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildFromSources();

    private void RebuildFromSources()
    {
        Results.Clear();

        foreach (var source in _sources)
        {
            foreach (var result in source.Results)
            {
                Results.Add(result);
            }
        }

        Recount();
    }

    private void Recount()
    {
        if (_sources.Count == 0)
        {
            return;
        }

        Total = _sources.Sum(s => s.Total);
        Done = _sources.Sum(s => s.Done);
        SuccessCount = _sources.Sum(s => s.SuccessCount);
        FailedCount = _sources.Sum(s => s.FailedCount);

        var running = _sources.Any(s => s.IsRunning);

        // сводный прогон ничего не выполняет сам: он «закончился», когда затихла последняя вкладка
        if (IsRunning && !running)
        {
            _finishedAt = DateTime.Now;
        }

        IsRunning = running;
    }

    public async Task RunAsync(
        string operationName,
        IReadOnlyList<ServerEntry> servers,
        ServerWork work,
        int maxParallel,
        Action<IReadOnlyList<ServerEntry>>? beforeRetry = null,
        Action? afterRetry = null)
    {
        if (IsRunning || servers.Count == 0)
        {
            return;
        }

        _operationName = operationName;
        _lastWork = work;
        _beforeRetry = beforeRetry;
        _afterRetry = afterRetry;

        // «Сохранять лог»: строки прошлого прогона по тем же серверам остаются вместе со своим
        // выводом, новый прогон дописывается под отбивкой. Серверы, которых в этом прогоне нет,
        // из таблицы уходят — в ней всегда ровно то, что выполняется сейчас.
        var carried = AppState.Current.KeepOutputLog ? Results.ToList() : new List<OperationResult>();
        var header = RunHeader(operationName);

        Results.Clear();

        foreach (var s in servers)
        {
            var kept = carried.FirstOrDefault(r => ReferenceEquals(r.Server, s));

            if (kept is null)
            {
                Results.Add(new OperationResult(s, Scope));
                continue;
            }

            // вкладку могли переименовать между прогонами — заголовок строки берём свежий
            kept.Scope = Scope ?? string.Empty;
            kept.ResetJob();
            kept.StartNextRun(header);
            Results.Add(kept);
        }

        await ExecuteAsync(Results.ToList(), work, maxParallel);
    }

    public async Task RetryFailedAsync(int maxParallel)
    {
        // сводный прогон сам ничего не выполняет — повторяем у тех, кто выполнял
        if (_sources.Count > 0)
        {
            await Task.WhenAll(_sources.Select(s => s.RetryFailedAsync(maxParallel)));
            return;
        }

        if (IsRunning || _lastWork is null)
        {
            return;
        }

        var failed = Results.Where(r => r.Status is OperationStatus.Failed or OperationStatus.Cancelled).ToList();
        if (failed.Count == 0)
        {
            return;
        }

        AppLog.Info($"Повтор операции «{_operationName}» на {failed.Count} сервере(ах).");

        var header = RunHeader("повтор: " + _operationName);

        foreach (var r in failed)
        {
            r.ResetJob();

            if (AppState.Current.KeepOutputLog)
            {
                r.StartNextRun(header);
            }
            else
            {
                r.Reset();
            }
        }

        // цепочке команд надо знать, кто повторяет: счётчики окон и итог на ярлыке
        _beforeRetry?.Invoke(failed.Select(r => r.Server).ToList());

        try
        {
            await ExecuteAsync(failed, _lastWork, maxParallel);
        }
        finally
        {
            _afterRetry?.Invoke();
        }
    }

    /// <summary>Отбивка между прогонами в выводе, когда лог сохраняется.</summary>
    private static string RunHeader(string operationName) =>
        $"───── {DateTime.Now:HH:mm:ss} · {operationName} ─────";

    /// <summary>Достаёт настоящую причину из обёрток вроде AggregateException.</summary>
    private static Exception Unwrap(Exception ex)
    {
        if (ex is AggregateException aggregate)
        {
            return Unwrap(aggregate.GetBaseException());
        }

        // сетевые сбои SSH.NET часто приходят завёрнутыми
        if (ex.InnerException is System.Net.Sockets.SocketException socket)
        {
            return socket;
        }

        return ex;
    }

    private async Task ExecuteAsync(IReadOnlyList<OperationResult> targets, ServerWork work, int maxParallel)
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _finishedAt = null;
        StartedAt = DateTime.Now;
        Raise(nameof(StartedAt));

        // итоги серверов придут в журнал уже после того, как человек мог уйти в другой раздел
        if (Section is { } section)
        {
            AppLog.Section = section;
        }

        IsRunning = true;
        Total = targets.Count;
        Done = 0;
        SuccessCount = 0;
        FailedCount = 0;

        AppLog.Debug($"«{_operationName}» — старт на {Total} сервере(ах), одновременно до {maxParallel}.");

        using var gate = new SemaphoreSlim(Math.Max(1, maxParallel));
        var doneLock = new Lock();

        var tasks = targets.Select(async result =>
        {
            await gate.WaitAsync(CancellationToken.None);
            var sw = Stopwatch.StartNew();

            try
            {
                if (ct.IsCancellationRequested)
                {
                    result.Status = OperationStatus.Cancelled;
                    result.Message = "не запускалось";
                    return;
                }

                result.Status = OperationStatus.Running;
                result.Message = "подключение…";
                AppLog.Debug($"старт: {_operationName}", result.Server.Display);

                await work(result.Server, result, ct);

                if (result.Status == OperationStatus.Running)
                {
                    result.Status = OperationStatus.Success;
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException || ct.IsCancellationRequested)
            {
                // после «Остановить» всё, что оборвалось, — выполненная просьба, а не сбой:
                // закрытый канал SSH.NET сообщает своей ошибкой, а не отменой
                result.Status = OperationStatus.Cancelled;
                result.Message = "отменено";
                result.Append("Операция отменена.");
            }
            catch (Exception ex)
            {
                var real = Unwrap(ex);
                var explained = SshService.Describe(real, result.Server);

                result.Status = OperationStatus.Failed;
                result.Message = explained;
                result.Append("ОШИБКА: " + explained);

                if (!string.Equals(explained, real.Message, StringComparison.Ordinal))
                {
                    result.Append($"Исходная ошибка ({real.GetType().Name}): {real.Message}");
                }

                if (real is Renci.SshNet.Common.SshAuthenticationException)
                {
                    var methods = await SshService.TryProbeAuthMethodsAsync(result.Server, AppState.Current.Settings);

                    if (methods.Count > 0)
                    {
                        result.Append("Сервер разрешает способы входа: " + string.Join(", ", methods));

                        if (!result.Server.HasKey && !methods.Contains("password") && !methods.Contains("keyboard-interactive"))
                        {
                            result.Append("Вход по паролю на этом сервере отключён — укажите ключ SSH в настройках сервера.");
                        }
                        else if (result.Server.HasKey && !methods.Contains("publickey"))
                        {
                            result.Append("Вход по ключу на этом сервере отключён.");
                        }
                    }
                }
            }
            finally
            {
                sw.Stop();
                result.Duration = sw.Elapsed;
                result.Server.LastStatus = result.StatusText;

                lock (doneLock)
                {
                    Done++;

                    if (result.Status == OperationStatus.Success)
                    {
                        SuccessCount++;
                    }
                    else
                    {
                        FailedCount++;
                    }
                }

                // с одним сервером его итог и есть итог операции — он придёт ниже, один раз
                if (Total > 1)
                {
                    AppLog.ServerLine(LevelOf(result), Outcome(result), result.Server.Display);
                }

                gate.Release();
            }
        });

        await Task.WhenAll(tasks);

        _finishedAt = DateTime.Now;
        IsRunning = false;
        Raise(nameof(CanRetry));

        if (targets.Count == 1)
        {
            var only = targets[0];
            AppLog.Write(LevelOf(only), $"{_operationName}: {Outcome(only)}", only.Server.Display);
        }
        else if (FailedCount == 0)
        {
            AppLog.Success($"{_operationName}: все {SuccessCount} сервер(ов) — успешно.");
        }
        else
        {
            var bad = Results
                .Where(r => r.Status is OperationStatus.Failed or OperationStatus.Cancelled)
                .Select(r => r.Server.PaneLabel);

            AppLog.Error($"{_operationName}: успешно {SuccessCount} из {Total}. Не прошли: {string.Join(", ", bad)}.");
        }
    }

    /// <summary>
    /// Итог сервера словами — то же, что в его строке терминала: «получено 0 · такие же 3»,
    /// а не «готово». По нему видно, что именно сделано, без похода в терминал.
    /// </summary>
    private static string Outcome(OperationResult result)
    {
        var text = result.Message.Trim().TrimEnd('…').Trim();

        if (text.Length == 0)
        {
            text = result.Status switch
            {
                OperationStatus.Success => "выполнено",
                OperationStatus.Cancelled => "отменено",
                _ => "не выполнено",
            };
        }

        return $"{text} ({result.Duration.TotalSeconds:0.0} с)";
    }

    private static LogLevel LevelOf(OperationResult result) => result.Status switch
    {
        OperationStatus.Success => LogLevel.Success,
        OperationStatus.Cancelled => LogLevel.Warning,
        _ => LogLevel.Error,
    };
}
