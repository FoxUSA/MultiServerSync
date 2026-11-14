using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using Pipe = System.IO.Pipelines.Pipe;
using StreamPipeOptions = System.IO.Pipelines.PipeOptions;
using System.Security.Principal;
using System.Text;
using System.Windows;
using MultiServerSync.Models;

namespace MultiServerSync.Services;

/// <summary>
/// «Режим админа» у «PowerShell»: команды идут в PowerShell с правами администратора. Права даёт помощник —
/// та же программа, запущенная через окно UAC один раз при включении. Он живёт, пока режим включён, и
/// выполняет команды по просьбе программы через именованный канал: вывод, ответы на вопросы и стоп
/// ходят по нему же. Программа запущена от администратора — наоборот: режим включён сам, а выключение
/// запускает помощника с правами пользователя (токен Проводника), и команды идут через него.
/// </summary>
public static class AdminShell
{
    /// <summary>Ключ командной строки: экземпляр программы — помощник, а не окно.</summary>
    public const string HelperArg = "--mss-admin-shell";

    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>Сама программа запущена от администратора.</summary>
    public static bool ProcessElevated { get; } = IsElevated();

    private static NamedPipeServerStream? _pipe;
    private static StreamWriter? _writer;
    private static readonly Lock SendLock = new();
    private static readonly ConcurrentDictionary<int, Job> Jobs = new();
    private static int _nextId;
    private static volatile bool _connected;

    /// <summary>Помощник на связи.</summary>
    public static bool Connected => _connected;

    /// <summary>
    /// Терминал «PowerShell» работает от администратора: программа обычная — пока жив помощник администратора,
    /// программа от администратора — пока не запущен помощник с правами пользователя.
    /// </summary>
    public static bool IsOn => ProcessElevated ? !_connected : _connected;

    /// <summary>Команды от администратора ещё идут — выключать режим нельзя.</summary>
    public static bool Busy => !Jobs.IsEmpty;

    /// <summary>Режим включился или выключился (в том числе сам: помощник закрылся). Приходит в потоке окна.</summary>
    public static event Action? Changed;

    /// <summary>
    /// Запустить помощника с другими правами, чем у программы: обычная программа — окно UAC и помощник
    /// администратора (режим включается), программа от администратора — помощник пользователя (режим
    /// выключается). Возвращает причину отказа или null.
    /// </summary>
    public static async Task<string?> StartAsync()
    {
        if (_connected)
        {
            return null;
        }

        var failed = ProcessElevated ? "Права не понизились: " : "Режим админа не включился: ";
        var exe = Environment.ProcessPath ?? "MultiServerSync.App.exe";
        var name = "mss-admin-" + Guid.NewGuid().ToString("N");
        NamedPipeServerStream pipe;
        Process? helper;

        try
        {
            pipe = ProcessElevated
                ? UserPipe(name)
                : new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        }
        catch (Exception ex)
        {
            AppLog.Debug("Помощник PowerShell: канал не создан — " + ex.Message);
            return failed + ex.Message;
        }

        try
        {
            var arguments = $"{HelperArg} {name} {Environment.ProcessId}";

            helper = ProcessElevated
                ? Process.GetProcessById(UserLaunch.Start(exe, arguments))
                : Process.Start(new ProcessStartInfo(exe)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    Arguments = arguments,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            pipe.Dispose();
            AppLog.Debug("Режим админа: в окне UAC отказали.");
            return "В окне Windows права администратора не дали — режим админа не включён.";
        }
        catch (Exception ex)
        {
            pipe.Dispose();
            AppLog.Debug("Помощник PowerShell не запустился — " + ex.Message);
            return failed + ex.Message;
        }

        try
        {
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await pipe.WaitForConnectionAsync(wait.Token);

            var reader = new StreamReader(pipe, Utf8, false);
            var hello = await reader.ReadLineAsync(wait.Token);

            // помощник обычной программы должен быть администратором, помощник программы от администратора — нет
            if (hello != (ProcessElevated ? "HELLO 0" : "HELLO 1"))
            {
                throw new InvalidOperationException(ProcessElevated
                    ? "помощник всё равно запустился от администратора — похоже, в Windows выключен UAC"
                    : "помощник запустился без прав администратора");
            }

            _pipe = pipe;
            _writer = new StreamWriter(pipe, Utf8) { AutoFlush = true, NewLine = "\n" };
            _connected = true;

            var listener = new Thread(() => Listen(reader)) { IsBackground = true, Name = "AdminShell" };
            listener.Start();
        }
        catch (Exception ex)
        {
            pipe.Dispose();

            try
            {
                helper?.Kill();
            }
            catch (Exception)
            {
                // уже закрылся
            }

            AppLog.Debug("Помощник PowerShell не вышел на связь — " + (ex is OperationCanceledException ? "не ответил за 30 с" : ex.Message));
            return failed + (ex is OperationCanceledException ? "помощник не ответил" : ex.Message) + ".";
        }

        AppLog.Debug(ProcessElevated
            ? $"Режим админа выключен: помощник с правами пользователя на связи, процесс {helper?.Id.ToString() ?? "?"}."
            : $"Режим админа включён: помощник на связи, процесс {helper?.Id.ToString() ?? "?"}.");
        Raise();
        return null;
    }

    /// <summary>
    /// Канал для помощника с правами пользователя: у канала программы от администратора по умолчанию писать
    /// может только администратор — пользователю рабочего стола запись даётся явно.
    /// </summary>
    private static NamedPipeServerStream UserPipe(string name)
    {
        var user = UserLaunch.DesktopUser() ?? throw new InvalidOperationException("не найден Проводник Windows — не у кого взять права пользователя");
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite, System.Security.AccessControl.AccessControlType.Allow));

        if (WindowsIdentity.GetCurrent().User is { } self)
        {
            security.AddAccessRule(new PipeAccessRule(self, PipeAccessRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
        }

        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    /// <summary>Закрыть помощника вместе с тем, что он ещё выполняет: команды снова идут из самой программы.</summary>
    public static void Stop()
    {
        if (!_connected)
        {
            return;
        }

        Send("QUIT");
        Disconnect();
    }

    private static void Disconnect()
    {
        if (!_connected)
        {
            return;
        }

        _connected = false;
        AppLog.Debug($"Помощник PowerShell закрыт ({(ProcessElevated ? "права пользователя" : "права администратора")}), команд оборвано: {Jobs.Count}.");

        try
        {
            _pipe?.Dispose();
        }
        catch (Exception)
        {
            // уже закрыт
        }

        _pipe = null;
        _writer = null;

        foreach (var job in Jobs.Values)
        {
            job.Break();
        }

        Raise();
    }

    private static void Raise()
    {
        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            dispatcher.BeginInvoke(() => Changed?.Invoke());
        }
    }

    private static void Send(string line)
    {
        lock (SendLock)
        {
            try
            {
                _writer?.WriteLine(line);
            }
            catch (Exception)
            {
                // канал оборвался — заметит слушатель
            }
        }
    }

    private static void Listen(StreamReader reader)
    {
        try
        {
            while (reader.ReadLine() is { } line)
            {
                var parts = line.Split(' ', 3);

                if (parts.Length < 3 || !int.TryParse(parts[1], out var id) || !Jobs.TryGetValue(id, out var job))
                {
                    continue;
                }

                switch (parts[0])
                {
                    case "PID" when int.TryParse(parts[2], out var pid):
                        job.Pid = pid;
                        job.OnJob?.Invoke(pid, pid);
                        break;
                    case "OUT":
                        job.Write(job.Out, Convert.FromBase64String(parts[2]));
                        break;
                    case "ERR":
                        job.Write(job.Err, Convert.FromBase64String(parts[2]));
                        break;
                    case "EXIT":
                        job.Finish(int.TryParse(parts[2], out var code) ? code : null);
                        break;
                }
            }
        }
        catch (Exception)
        {
            // канал закрыт
        }

        Disconnect();
    }

    /// <summary>Команда, которую выполняет помощник: её вывод приходит кусками, конец — кодом завершения.</summary>
    private sealed class Job(int id)
    {
        public int Id { get; } = id;

        public volatile int Pid;

        public Action<int, int>? OnJob;

        public readonly Pipe Out = new(new StreamPipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0));

        public readonly Pipe Err = new(new StreamPipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0));

        public readonly TaskCompletionSource<int?> Exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public volatile bool Broken;

        public void Write(Pipe pipe, byte[] bytes)
        {
            if (Exit.Task.IsCompleted)
            {
                return;
            }

            pipe.Writer.WriteAsync(bytes).AsTask().GetAwaiter().GetResult();
        }

        public void Finish(int? code)
        {
            if (Exit.TrySetResult(code))
            {
                Out.Writer.Complete();
                Err.Writer.Complete();
            }
        }

        public void Break()
        {
            Broken = true;
            Finish(null);
        }
    }

    /// <summary>То же, что <see cref="LocalShell.RunScriptAsync"/>, но PowerShell запускает помощник администратора.</summary>
    internal static Task<CommandOutcome> RunScriptAsync(
        AppSettings settings,
        string script,
        Action<string>? live,
        CancellationToken ct,
        Action<int, int>? onJob,
        Action<CommandInput?>? onInput,
        Action<bool>? onWaiting,
        Action<string>? onTail)
    {
        return Task.Run(() =>
        {
            var job = new Job(Interlocked.Increment(ref _nextId)) { OnJob = onJob };
            Jobs[job.Id] = job;

            try
            {
                // консоль у помощника — один поток, коды терминала вычищаются здесь
                var stdout = new StringBuilder();
                var outPump = new LinePump(stdout, live, null, onTail);
                var errPump = new LinePump(new StringBuilder(), null, null, null);

                var outReader = Task.Run(() => outPump.Run(new VtCleanStream(job.Out.Reader.AsStream())), CancellationToken.None);

                Send($"RUN {job.Id} {Convert.ToBase64String(Utf8.GetBytes(script))}");
                AppLog.Debug($"Режим админа: команда #{job.Id} отдана помощнику, {script.Length} символов.");

                if (!_connected)
                {
                    job.Break();
                }

                using var registration = ct.Register(() => Send($"KILL {job.Id} -"));

                var input = onInput is null
                    ? null
                    : new CommandInput(
                        new ConsoleInputStream(bytes =>
                        {
                            if (!_connected)
                            {
                                throw new IOException("Режим админа выключен.");
                            }

                            Send($"IN {job.Id} {Convert.ToBase64String(bytes)}");
                        }),
                        outPump,
                        errPump,
                        echoes: true);

                var timedOut = LocalShell.Supervise(settings, tick => job.Exit.Task.Wait(tick), () => Send($"KILL {job.Id} -"),
                    outPump, errPump, input, onInput, onWaiting);

                if (timedOut)
                {
                    job.Exit.Task.Wait(TimeSpan.FromSeconds(10));
                }

                outReader.Wait(TimeSpan.FromSeconds(20));
                ct.ThrowIfCancellationRequested();

                var code = job.Exit.Task.IsCompleted ? job.Exit.Task.Result : null;
                AppLog.Debug($"Режим админа: команда #{job.Id} (процесс {job.Pid}) закончилась, код {code?.ToString() ?? "неизвестен"}"
                    + (timedOut ? ", снята по таймауту" : string.Empty) + (job.Broken ? ", связь с помощником оборвалась" : string.Empty)
                    + $", вывода {LocalShell.Text(stdout).Length} символов.");

                if (job.Broken)
                {
                    throw new InvalidOperationException("Режим админа выключился посреди команды — она прервана.");
                }
                return new CommandOutcome(code, LocalShell.Text(stdout), string.Empty, timedOut, null);
            }
            finally
            {
                Jobs.TryRemove(job.Id, out _);
            }
        }, CancellationToken.None);
    }

    /// <summary>Процесс команды — у помощника: снять его может только он.</summary>
    public static bool Owns(int pid) => pid > 0 && Jobs.Values.Any(j => j.Pid == pid);

    /// <summary>Стоп команды администратора: INT — Ctrl+C в её консоль, остальное — снять процесс.</summary>
    public static bool Signal(int pid, string signal)
    {
        if (Jobs.Values.FirstOrDefault(j => j.Pid == pid) is not { } job)
        {
            return false;
        }

        Send($"{(signal == "INT" ? "INT" : "KILL")} {job.Id} -");
        return true;
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ── помощник: та же программа с правами администратора, без окна ──────────

    /// <summary>
    /// Помощник: подключается к каналу программы и выполняет присланные команды, пока программа не закроет
    /// канал, не попросит выйти или сама не закроется.
    /// </summary>
    public static void RunHelper(string pipeName, int parentPid)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        pipe.Connect(15_000);

        var writer = new StreamWriter(pipe, Utf8) { AutoFlush = true, NewLine = "\n" };
        var reader = new StreamReader(pipe, Utf8, false);
        var sendLock = new Lock();
        var processes = new ConcurrentDictionary<int, ConPtyProcess>();

        void Reply(string line)
        {
            lock (sendLock)
            {
                try
                {
                    writer.WriteLine(line);
                }
                catch (Exception)
                {
                    // программа закрыла канал
                }
            }
        }

        Reply("HELLO " + (IsElevated() ? "1" : "0"));

        // программа закрылась, не попрощавшись, — помощник не остаётся висеть
        _ = Task.Run(() =>
        {
            try
            {
                using var parent = Process.GetProcessById(parentPid);
                parent.WaitForExit();
            }
            catch (ArgumentException)
            {
                // её уже нет
            }
            catch (Exception)
            {
                // программу от администратора помощнику с правами пользователя может быть не видно — хватит обрыва канала
                return;
            }

            try
            {
                pipe.Dispose();
            }
            catch (Exception)
            {
                // уже закрыт
            }
        });

        try
        {
            while (reader.ReadLine() is { } line)
            {
                var parts = line.Split(' ', 3);

                if (parts[0] == "QUIT")
                {
                    break;
                }

                if (parts.Length < 3 || !int.TryParse(parts[1], out var id))
                {
                    continue;
                }

                switch (parts[0])
                {
                    case "RUN":
                        Start(id, Utf8.GetString(Convert.FromBase64String(parts[2])));
                        break;
                    case "IN" when processes.TryGetValue(id, out var target):
                        try
                        {
                            LocalShell.Send(target, Convert.FromBase64String(parts[2]));
                        }
                        catch (Exception)
                        {
                            // консоль уже закрыта
                        }

                        break;
                    case "INT" when processes.TryGetValue(id, out var target):
                        target.Interrupt();
                        break;
                    case "KILL" when processes.TryGetValue(id, out var target):
                        target.Kill();
                        break;
                }
            }
        }
        catch (Exception)
        {
            // канал закрыт
        }

        foreach (var process in processes.Values)
        {
            process.Kill();
        }

        // процесс запускается здесь же, в порядке строк: ввод и стоп, пришедшие следом, уже найдут его.
        // Консоль та же, что без прав администратора: вывод уходит как есть, коды терминала чистит программа
        void Start(int id, string script)
        {
            var (commandLine, file, directory) = LocalShell.Prepare(script);
            ConPtyProcess process;

            try
            {
                process = ConPtyProcess.Start(commandLine, directory);
            }
            catch (Exception ex)
            {
                LocalShell.Forget(file);
                Reply($"OUT {id} {Convert.ToBase64String(Utf8.GetBytes("PowerShell не запустился: " + ex.Message + "\r\n"))}");
                Reply($"EXIT {id} -1");
                return;
            }

            processes[id] = process;
            Reply($"PID {id} {process.Pid}");

            var lastData = DateTime.UtcNow;

            _ = Task.Run(() =>
            {
                var output = Task.Run(Pump);

                while (!process.WaitForExit(TimeSpan.FromSeconds(1)))
                {
                }

                // вывод кончается, только когда закрыта консоль
                process.CloseConsole(() => lastData);
                output.Wait(TimeSpan.FromSeconds(20));

                Reply($"EXIT {id} {process.ExitCode?.ToString() ?? "-"}");
                processes.TryRemove(id, out _);
                process.Dispose();
                LocalShell.Forget(file);
            });

            void Pump()
            {
                var buffer = new byte[8192];

                try
                {
                    int read;

                    while ((read = process.Output.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        lastData = DateTime.UtcNow;
                        Reply($"OUT {id} {Convert.ToBase64String(buffer, 0, read)}");
                    }
                }
                catch (Exception)
                {
                    // консоль закрыта
                }
            }
        }
    }
}
