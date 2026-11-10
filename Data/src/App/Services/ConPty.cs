using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MultiServerSync.Services;

/// <summary>
/// Процесс в настоящей консоли Windows (ConPTY): программы видят терминал, а не трубу — `ssh` спрашивает
/// пароль, `diskpart` и `netsh` ведут диалог. Ввод и вывод — обычными потоками; вывод — UTF-8 с кодами
/// терминала, их вычищает <see cref="VtCleanStream"/>.
/// </summary>
internal sealed class ConPtyProcess : IDisposable
{
    /// <summary>Ширина консоли: длинные строки не рвутся переносом.</summary>
    private const short Width = 300;

    /// <summary>Высота: чем выше экран, тем реже консоль прокручивает его посреди вывода.</summary>
    internal const short Height = 120;

    private IntPtr _console;
    private readonly SafeProcessHandle _process;
    private readonly Lock _lock = new();

    public int Pid { get; }

    /// <summary>Вывод как он есть, с кодами терминала.</summary>
    public Stream Output { get; }

    /// <summary>Ввод: байты идут в консоль, как с клавиатуры.</summary>
    public Stream Input { get; }

    private ConPtyProcess(IntPtr console, SafeProcessHandle process, int pid, Stream output, Stream input)
    {
        _console = console;
        _process = process;
        Pid = pid;
        Output = output;
        Input = input;
    }

    public static ConPtyProcess Start(string commandLine, string workingDirectory)
    {
        if (!CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0) || !CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var hr = CreatePseudoConsole(new Coord { X = Width, Y = Height }, inRead, outWrite, 0, out var console);

        // консоль держит свои копии: наши концы, что у неё, больше не нужны
        inRead.Dispose();
        outWrite.Dispose();

        if (hr != 0)
        {
            inWrite.Dispose();
            outRead.Dispose();
            throw new Win32Exception(hr, "Консоль Windows не создалась.");
        }

        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var attributes = Marshal.AllocHGlobal(size);

        try
        {
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)
                || !UpdateProcThreadAttribute(attributes, 0, (IntPtr)PseudoConsoleAttribute, console, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var startup = new StartupInfoEx { AttributeList = attributes };
            startup.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();

            // стандартные потоки — пустые: иначе процесс унаследует перенаправленные потоки родителя
            // и будет писать мимо консоли
            startup.StartupInfo.dwFlags = UseStdHandles;

            if (!CreateProcessW(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                    ExtendedStartupInfoPresent | CreateUnicodeEnvironment, IntPtr.Zero, workingDirectory, ref startup, out var info))
            {
                var error = Marshal.GetLastWin32Error();
                ClosePseudoConsole(console);
                inWrite.Dispose();
                outRead.Dispose();
                throw new Win32Exception(error);
            }

            CloseHandle(info.hThread);

            return new ConPtyProcess(
                console,
                new SafeProcessHandle(info.hProcess, ownsHandle: true),
                info.dwProcessId,
                new FileStream(outRead, FileAccess.Read, 1),
                new FileStream(inWrite, FileAccess.Write, 1));
        }
        finally
        {
            DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
        }
    }

    /// <summary>Процесс закончился за это время.</summary>
    public bool WaitForExit(TimeSpan timeout) =>
        WaitForSingleObject(_process, (uint)Math.Clamp(timeout.TotalMilliseconds, 0, uint.MaxValue - 1)) == 0;

    public bool HasExited => WaitForExit(TimeSpan.Zero);

    public int? ExitCode => HasExited && GetExitCodeProcess(_process, out var code) ? unchecked((int)code) : null;

    /// <summary>
    /// Консоль закрывается — только после этого вывод кончается. Ждём, пока он затихнет: последние строки
    /// консоль досылает уже после выхода процесса.
    /// </summary>
    public void CloseConsole(Func<DateTime> lastData)
    {
        var until = DateTime.UtcNow.AddSeconds(2);

        while (DateTime.UtcNow < until && DateTime.UtcNow - lastData() < TimeSpan.FromMilliseconds(200))
        {
            Thread.Sleep(50);
        }

        IntPtr console;

        lock (_lock)
        {
            console = _console;
            _console = IntPtr.Zero;
        }

        if (console != IntPtr.Zero)
        {
            ClosePseudoConsole(console);
        }
    }

    /// <summary>Ctrl+C — как с клавиатуры: команда сама решает, как остановиться.</summary>
    public void Interrupt()
    {
        try
        {
            Input.Write([0x03]);
            Input.Flush();
        }
        catch (Exception)
        {
            // консоль уже закрыта
        }
    }

    /// <summary>Снять процесс вместе со всем, что он запустил.</summary>
    public bool Kill()
    {
        try
        {
            using var process = Process.GetProcessById(Pid);

            if (process.HasExited)
            {
                return false;
            }

            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        CloseConsole(() => DateTime.MinValue);

        try
        {
            Input.Dispose();
        }
        catch (Exception)
        {
            // уже закрыт
        }

        try
        {
            Output.Dispose();
        }
        catch (Exception)
        {
            // уже закрыт
        }

        _process.Dispose();
    }

    // ── Win32 ──────────────────────────────────────────────────────────────────

    private const int PseudoConsoleAttribute = 0x00020016;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int UseStdHandles = 0x00000100;

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, int size);

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr console);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint flags, IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInformation info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle handle, out uint code);
}

/// <summary>
/// Вывод консоли без кодов терминала: цвет, курсор, заголовок окна вырезаются, сдвиг курсора вправо
/// становится пробелами (так консоль передаёт пробелы посреди строки). Остаются текст и переводы строк.
/// </summary>
internal sealed class VtCleanStream(Stream inner) : Stream
{
    private enum State
    {
        Text,
        Escape,
        Csi,
        Osc,
        OscEscape,
        Charset,
        Control,
        ControlEscape,
    }

    private State _state;
    private readonly StringBuilder _params = new();

    /// <summary>Где курсор на экране консоли: пустые строки она передаёт прыжком курсора вниз, а не переводом строки.</summary>
    private int _row = 1;
    private int _column = 1;
    private readonly Queue<byte> _ready = new();
    private long _lastData = DateTime.MinValue.Ticks;

    /// <summary>Когда в последний раз пришли байты — консоль закрывают, когда вывод затих.</summary>
    public DateTime LastData => new(Interlocked.Read(ref _lastData), DateTimeKind.Utc);

    public override int Read(byte[] buffer, int offset, int count)
    {
        while (_ready.Count == 0)
        {
            var raw = new byte[4096];
            int read;

            try
            {
                read = inner.Read(raw, 0, raw.Length);
            }
            catch (IOException)
            {
                // консоль закрыта — вывод кончился
                read = 0;
            }
            catch (ObjectDisposedException)
            {
                read = 0;
            }

            if (read == 0)
            {
                return 0;
            }

            Interlocked.Exchange(ref _lastData, DateTime.UtcNow.Ticks);

            for (var i = 0; i < read; i++)
            {
                Feed(raw[i]);
            }
        }

        var n = 0;

        while (n < count && _ready.Count > 0)
        {
            buffer[offset + n++] = _ready.Dequeue();
        }

        return n;
    }

    private void Feed(byte b)
    {
        switch (_state)
        {
            case State.Text:
                if (b == 0x1B)
                {
                    _state = State.Escape;
                }
                else if (b != 0x07)
                {
                    _ready.Enqueue(b);

                    if (b == (byte)'\n')
                    {
                        // внизу экрана консоль прокручивает его — курсор остаётся на последней строке
                        _row = Math.Min(_row + 1, ConPtyProcess.Height);
                    }
                    else if (b == (byte)'\r')
                    {
                        _column = 1;
                    }
                    else if (b is < 0x80 or >= 0xC0)
                    {
                        // буква, а не продолжение многобайтной в UTF-8
                        _column++;
                    }
                }

                break;

            case State.Escape:
                _state = b switch
                {
                    (byte)'[' => State.Csi,
                    (byte)']' => State.Osc,
                    (byte)'P' or (byte)'X' or (byte)'^' or (byte)'_' => State.Control,
                    (byte)'(' or (byte)')' or (byte)'*' or (byte)'+' or (byte)'#' or (byte)'%' => State.Charset,
                    _ => State.Text,
                };

                _params.Clear();
                break;

            case State.Csi:
                if (b is >= 0x40 and <= 0x7E)
                {
                    // сдвиг курсора вправо — пробелы; прыжок на строку ниже — переводы строк (так консоль
                    // передаёт пустые строки); остальное (цвет, прыжки вверх, очистка) не нужно
                    if (b == (byte)'C')
                    {
                        Spaces(Number(_params.ToString(), 1));
                    }
                    else if (b is (byte)'H' or (byte)'f')
                    {
                        var parts = _params.ToString().Split(';');
                        var row = Number(parts[0], 1);
                        var column = Number(parts.Length > 1 ? parts[1] : string.Empty, 1);

                        if (row > _row)
                        {
                            for (var i = 0; i < Math.Min(row - _row, 1000); i++)
                            {
                                _ready.Enqueue((byte)'\r');
                                _ready.Enqueue((byte)'\n');
                            }

                            _row = row;
                            _column = 1;
                        }
                        else if (row < _row)
                        {
                            // перерисовка выше по экрану — в построчном выводе её не повторить
                            _row = row;
                            _column = column;
                            _state = State.Text;
                            break;
                        }

                        if (column > _column)
                        {
                            Spaces(column - _column);
                        }
                    }

                    _state = State.Text;
                }
                else
                {
                    _params.Append((char)b);
                }

                break;

            case State.Osc:
                if (b == 0x07)
                {
                    _state = State.Text;
                }
                else if (b == 0x1B)
                {
                    _state = State.OscEscape;
                }

                break;

            case State.OscEscape:
                _state = b == (byte)'\\' ? State.Text : State.Osc;
                break;

            case State.Charset:
                _state = State.Text;
                break;

            case State.Control:
                if (b == 0x1B)
                {
                    _state = State.ControlEscape;
                }

                break;

            case State.ControlEscape:
                _state = b == (byte)'\\' ? State.Text : State.Control;
                break;
        }
    }

    private static int Number(string text, int fallback) =>
        int.TryParse(text, out var parsed) && parsed > 0 ? parsed : fallback;

    private void Spaces(int count)
    {
        for (var i = 0; i < Math.Min(count, 1000); i++)
        {
            _ready.Enqueue((byte)' ');
        }

        _column += Math.Min(count, 1000);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Ввод в консоль из строки ответа: Enter в консоли — это CR, а не LF. Конец ввода трубу не закрывает:
/// консоль от этого не кончается, а закрыть её — значит оборвать команду.
/// </summary>
internal sealed class ConsoleInputStream(Action<byte[]> send) : Stream
{
    public override void Write(byte[] buffer, int offset, int count)
    {
        var bytes = new byte[count];

        for (var i = 0; i < count; i++)
        {
            var b = buffer[offset + i];
            bytes[i] = b == (byte)'\n' ? (byte)'\r' : b;
        }

        send(bytes);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
