using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using MultiServerSync.Models;

namespace MultiServerSync.Services;

/// <summary>
/// Команда «PowerShell»: выполняется на этом компьютере через PowerShell в настоящей консоли Windows (ConPTY),
/// а не на сервере. Программы видят терминал: `ssh` спрашивает пароль, `diskpart` ведёт диалог. Вывод, ответ
/// на вопрос, таймаут и стоп — те же, что у команды на сервере, чтобы терминал и журнал не различали их.
/// В «Режиме админа» тот же PowerShell запускает помощник с правами администратора (<see cref="AdminShell"/>).
/// </summary>
public static class LocalShell
{
    /// <summary>Идущие команды по номеру процесса — стоп в строке посылает им Ctrl+C или снимает.</summary>
    private static readonly ConcurrentDictionary<int, ConPtyProcess> Running = new();

    public static Task<CommandOutcome> RunScriptAsync(
        AppSettings settings,
        string script,
        Action<string>? live,
        CancellationToken ct,
        Action<int, int>? onJob = null,
        Action<CommandInput?>? onInput = null,
        Action<bool>? onWaiting = null,
        Action<string>? onTail = null)
    {
        ct.ThrowIfCancellationRequested();

        if (AdminShell.Connected)
        {
            return AdminShell.RunScriptAsync(settings, script, live, ct, onJob, onInput, onWaiting, onTail);
        }

        return Task.Run(() =>
        {
            var (commandLine, file, directory) = Prepare(script);

            try
            {
                ConPtyProcess started;

                try
                {
                    started = ConPtyProcess.Start(commandLine, directory);
                }
                catch (Exception ex)
                {
                    AppLog.Debug("PowerShell: консоль PowerShell не запустилась — " + ex.Message);
                    throw;
                }

                using var process = started;
                Running[process.Pid] = process;
                AppLog.Debug($"PowerShell: PowerShell в консоли, процесс {process.Pid}, {script.Length} символов команды.");

                try
                {
                    // по номеру процесса стоп в строке посылает Ctrl+C или снимает его со всем, что он запустил
                    onJob?.Invoke(process.Pid, process.Pid);

                    // консоль — один поток: обычный вывод и ошибки идут вместе, как на экране
                    var stdout = new StringBuilder();
                    var outPump = new LinePump(stdout, live, null, onTail);
                    var errPump = new LinePump(new StringBuilder(), null, null, null);
                    var clean = new VtCleanStream(process.Output);

                    var outReader = Task.Run(() => outPump.Run(clean), CancellationToken.None);

                    using var registration = ct.Register(() => process.Kill());

                    var input = onInput is null
                        ? null
                        : new CommandInput(new ConsoleInputStream(bytes => Send(process, bytes)), outPump, errPump, echoes: true);

                    var timedOut = Supervise(settings, process.WaitForExit, () => process.Kill(),
                        outPump, errPump, input, onInput, onWaiting);

                    if (timedOut)
                    {
                        process.WaitForExit(TimeSpan.FromSeconds(10));
                    }

                    // вывод кончается, только когда закрыта консоль
                    process.CloseConsole(() => clean.LastData);
                    var drained = outReader.Wait(TimeSpan.FromSeconds(20));

                    AppLog.Debug($"PowerShell: процесс {process.Pid} закончился, код {process.ExitCode?.ToString() ?? "неизвестен"}"
                        + (timedOut ? ", не дождались — снят по таймауту" : string.Empty)
                        + (ct.IsCancellationRequested ? ", остановлен" : string.Empty)
                        + (drained ? string.Empty : ", вывод не дочитан за 20 с")
                        + $", вывода {Text(stdout).Length} символов.");

                    ct.ThrowIfCancellationRequested();

                    return new CommandOutcome(process.ExitCode, Text(stdout), string.Empty, timedOut, null);
                }
                finally
                {
                    Running.TryRemove(process.Pid, out _);
                }
            }
            finally
            {
                Forget(file);
            }
        }, CancellationToken.None);
    }

    internal static void Send(ConPtyProcess process, byte[] bytes)
    {
        process.Input.Write(bytes);
        process.Input.Flush();
    }

    /// <summary>
    /// Текст уходит во временный .ps1 (UTF-8 с BOM — иначе PowerShell 5.1 читает кириллицу как ANSI).
    /// Полоса прогресса выключена: в консоли она рисуется поверх строк и в выводе была бы мусором.
    /// </summary>
    internal static (string CommandLine, string File, string Directory) Prepare(string script)
    {
        var file = Path.Combine(Path.GetTempPath(), "mss-" + Guid.NewGuid().ToString("N") + ".ps1");
        var prologue =
            "[Console]::InputEncoding = New-Object System.Text.UTF8Encoding($false)\r\n"
            + "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\r\n"
            + "$OutputEncoding = [System.Text.Encoding]::UTF8\r\n"
            + "$ProgressPreference = 'SilentlyContinue'\r\n";

        File.WriteAllText(file, prologue + script.Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n", new UTF8Encoding(true));

        var commandLine = $"powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File \"{file}\"";
        return (commandLine, file, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    internal static void Forget(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception)
        {
            // занят антивирусом — останется во временной папке
        }
    }

    /// <summary>
    /// Ждёт конца команды: показывает висящие строки, замечает вопрос, считает таймаут. Время, пока команда
    /// ждёт ответа человека, в таймаут не идёт — как на сервере. True — не дождались и сняли.
    /// </summary>
    internal static bool Supervise(
        AppSettings settings,
        Func<TimeSpan, bool> waitExit,
        Action kill,
        LinePump outPump,
        LinePump errPump,
        CommandInput? input,
        Action<CommandInput?>? onInput,
        Action<bool>? onWaiting)
    {
        if (input is not null)
        {
            onInput?.Invoke(input);
        }

        try
        {
            var timeout = TimeSpan.FromSeconds(Math.Max(5, settings.CommandTimeoutSeconds));
            var clock = Stopwatch.StartNew();
            var paused = TimeSpan.Zero;
            var waiting = false;
            var tick = TimeSpan.FromMilliseconds(250);

            try
            {
                while (!waitExit(tick))
                {
                    outPump.Tick();
                    errPump.Tick();

                    var nowWaiting = input is { IsOpen: true } && (outPump.Waiting || errPump.Waiting);

                    if (nowWaiting != waiting)
                    {
                        waiting = nowWaiting;
                        onWaiting?.Invoke(waiting);
                    }

                    if (waiting)
                    {
                        paused += tick;
                    }

                    if (clock.Elapsed - paused > timeout)
                    {
                        kill();
                        return true;
                    }
                }
            }
            finally
            {
                if (waiting)
                {
                    onWaiting?.Invoke(false);
                }
            }

            return false;
        }
        finally
        {
            input?.Close();

            if (onInput is not null)
            {
                onInput(null);
            }
        }
    }

    /// <summary>
    /// Стоп в строке: INT — Ctrl+C, как с клавиатуры (команда, `ssh` и сервер за ним остановятся сами);
    /// TERM и KILL — процесс снимается вместе со всем, что он запустил.
    /// </summary>
    public static bool Stop(int pid, string signal)
    {
        AppLog.Debug($"PowerShell: стоп {(signal == "INT" ? "Ctrl+C" : signal)} процессу {pid}{(AdminShell.Owns(pid) ? " (через помощника администратора)" : string.Empty)}.");

        // процесс администратора отсюда не тронуть — им распоряжается помощник
        if (AdminShell.Owns(pid))
        {
            return AdminShell.Signal(pid, signal);
        }

        if (Running.TryGetValue(pid, out var process))
        {
            if (signal == "INT")
            {
                process.Interrupt();
                return true;
            }

            return process.Kill();
        }

        try
        {
            using var stray = Process.GetProcessById(pid);
            stray.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static string Text(StringBuilder sink)
    {
        lock (sink)
        {
            return sink.ToString();
        }
    }
}
