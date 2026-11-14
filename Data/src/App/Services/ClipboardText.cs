using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace MultiServerSync.Services;

/// <summary>
/// Копирование в буфер обмена. Буфер на доли секунды открывают другие программы (менеджеры буфера,
/// журнал буфера Windows, удалённый рабочий стол) — поэтому несколько попыток, а отказ Windows после
/// того, как текст уже лёг в буфер, считается удачей. Не вышло — плашка с тем, кто держит буфер.
/// </summary>
public static class ClipboardText
{
    private static string? _terminalOutput;
    private static bool _terminalOutputAuto;

    /// <summary>
    /// Текст — прямо в буфер Windows (OpenClipboard/SetClipboardData), без прослойки OLE: через неё WPF
    /// отказывал то «буфер занят» (CLIPBRD_E_CANT_OPEN), то «Класс не зарегистрирован». Не вышло и так —
    /// последняя попытка через WPF.
    /// </summary>
    public static bool Copy(string text)
    {
        const int attempts = 6;
        string? reason = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            if (SetText(text, out reason))
            {
                return true;
            }

            AppLog.Debug($"Буфер обмена (попытка {attempt} из {attempts}){Holder()}: {reason}");

            if (attempt < attempts)
            {
                System.Threading.Thread.Sleep(50 * attempt);
            }
        }

        AppLog.Debug("Буфер обмена: прямая запись не вышла (" + reason + "), пробую через WPF.");
        return Put(text, () => ReadText(1) == text, "Не удалось скопировать в буфер обмена");
    }

    private const uint CfUnicodeText = 13;
    private const uint CfHdrop = 15;
    private const uint GmemMoveable = 0x0002;

    /// <summary>Есть ли в буфере текст; буфер для этого не открывается, занятость не мешает.</summary>
    public static bool HasText => IsClipboardFormatAvailable(CfUnicodeText);

    /// <summary>Есть ли в буфере файлы (скопированные в Проводнике Windows).</summary>
    public static bool HasFiles => IsClipboardFormatAvailable(CfHdrop);

    /// <summary>
    /// Текст из буфера; нет текста — пустая строка. Как и запись — средствами Windows, без OLE; буфер занят
    /// дольше секунды — последняя попытка через WPF (её отказ уходит исключением вызывающему).
    /// </summary>
    public static string ReadText(int attempts = 6)
    {
        if (!HasText)
        {
            return string.Empty;
        }

        var text = Read(CfUnicodeText, attempts, handle =>
        {
            var source = GlobalLock(handle);

            if (source == IntPtr.Zero)
            {
                return string.Empty;
            }

            try
            {
                return Marshal.PtrToStringUni(source) ?? string.Empty;
            }
            finally
            {
                GlobalUnlock(handle);
            }
        });

        return text ?? (Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty);
    }

    /// <summary>Файлы из буфера (скопированные в Проводнике Windows); нет — пустой список.</summary>
    public static StringCollection ReadFiles(int attempts = 6)
    {
        var files = new StringCollection();

        if (!HasFiles)
        {
            return files;
        }

        var read = Read(CfHdrop, attempts, drop =>
        {
            var count = DragQueryFile(drop, uint.MaxValue, null, 0);

            for (uint i = 0; i < count; i++)
            {
                var length = DragQueryFile(drop, i, null, 0);
                var name = new StringBuilder((int)length + 1);
                DragQueryFile(drop, i, name, (uint)name.Capacity);
                files.Add(name.ToString());
            }

            return "ok";
        });

        return read is null ? Clipboard.GetFileDropList() : files;
    }

    /// <summary>Открыть буфер (с повторами, пока занят) и прочитать формат; не открылся — null.</summary>
    private static string? Read(uint format, int attempts, Func<IntPtr, string> read)
    {

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    var handle = GetClipboardData(format);
                    return handle == IntPtr.Zero ? string.Empty : read(handle);
                }
                finally
                {
                    CloseClipboard();
                }
            }

            if (attempt < attempts)
            {
                System.Threading.Thread.Sleep(50 * attempt);
            }
        }

        AppLog.Debug($"Буфер обмена не открылся для чтения{Holder()}.");
        return null;
    }

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "DragQueryFileW")]
    private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, uint cch);

    /// <summary>Одна попытка положить текст в буфер средствами Windows.</summary>
    private static bool SetText(string text, out string? reason)
    {
        var owner = IntPtr.Zero;

        try
        {
            if (Application.Current?.MainWindow is { } main)
            {
                owner = new WindowInteropHelper(main).Handle;
            }
        }
        catch (Exception)
        {
            // окна ещё нет — буфер откроется без владельца
        }

        if (!OpenClipboard(owner))
        {
            reason = "буфер открыт другой программой";
            return false;
        }

        try
        {
            if (!EmptyClipboard())
            {
                reason = "буфер не очистился, ошибка Windows " + Marshal.GetLastWin32Error();
                return false;
            }

            var memory = GlobalAlloc(GmemMoveable, (UIntPtr)((text.Length + 1) * 2));

            if (memory == IntPtr.Zero)
            {
                reason = "не хватило памяти под текст";
                return false;
            }

            var target = GlobalLock(memory);

            if (target == IntPtr.Zero)
            {
                GlobalFree(memory);
                reason = "память под текст не заблокировалась";
                return false;
            }

            Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
            Marshal.WriteInt16(target, text.Length * 2, 0);
            GlobalUnlock(memory);

            // память теперь принадлежит буферу; не взял — освобождаем сами
            if (SetClipboardData(CfUnicodeText, memory) == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                GlobalFree(memory);
                reason = "буфер не принял текст, ошибка Windows " + error;
                return false;
            }

            reason = null;
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    /// <summary>
    /// Положить в буфер с повторами. <paramref name="landed"/> — лежит ли уже наше: Windows бывает отказывает
    /// на последнем шаге, когда данные уже в буфере (их тут же открыла программа, что следит за буфером).
    /// </summary>
    private static bool Put(object data, Func<bool> landed, string failed)
    {
        const int attempts = 6;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return true;
            }
            catch (Exception ex)
            {
                if (Landed(landed))
                {
                    AppLog.Debug("Буфер обмена: Windows ответила отказом, но данные уже в буфере — " + ex.Message);
                    return true;
                }

                AppLog.Debug($"Буфер обмена занят (попытка {attempt} из {attempts}){Holder()}: {ex.Message}");

                if (attempt < attempts)
                {
                    System.Threading.Thread.Sleep(50 * attempt);
                }
            }
        }

        AppLog.Hint($"{failed}: буфер занят другой программой{Holder()}. Попробуйте ещё раз.", LogLevel.Error);
        return false;
    }

    private static bool Landed(Func<bool> landed)
    {
        try
        {
            return landed();
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>« (держит: имя.exe)» — программа, у которой сейчас открыт буфер; не узнать — пусто.</summary>
    private static string Holder()
    {
        try
        {
            var window = GetOpenClipboardWindow();

            if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out var pid) == 0)
            {
                return "";
            }

            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return process.Id == Environment.ProcessId ? "" : $" ({process.ProcessName})";
        }
        catch (Exception)
        {
            return "";
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetOpenClipboardWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>
    /// Вывод терминала в буфер. Текст запоминается: вставить его в поле команды — почти
    /// всегда промах (думали, что в буфере команда, а там вывод, скопированный сам).
    /// </summary>
    /// <param name="auto">Скопировано галочкой «авто», а не кнопкой.</param>
    /// <param name="tab">Вкладка операции — для CopyPass при автокопии; нет — открытая.</param>
    public static bool CopyTerminalOutput(string text, bool auto = false, Workspace? tab = null)
    {
        if (!Copy(text))
        {
            return false;
        }

        _terminalOutput = Normalize(text);
        _terminalOutputAuto = auto;

        // CopyPass — только для вывода, скопированного галочкой «авто» по окончании операции.
        // Кнопкой копируют сами и вставляют сами; к тому же вывод мог быть давним
        if (auto)
        {
            CopyPass.Arm(tab ?? AppState.Current.ActiveWorkspace);
        }
        else
        {
            // в буфере уже не тот лог, что ждал поля ввода
            CopyPass.Disarm();
        }

        return true;
    }

    /// <summary>Файл с логом, что сейчас лежит в буфере: живёт, пока буфер не сменился.</summary>
    private static string? _logFile;

    /// <summary>Папка временных файлов логов — во временной папке Windows, не в UserData.</summary>
    private static string LogDir => Path.Combine(Path.GetTempPath(), "MultiServerSync", "clipboard");

    /// <summary>
    /// Длинный лог автокопии — файлом (галочка «*.txt»): в чат он вставляется вложением целиком и
    /// не подвешивает его. Файл удаляется, как только в буфере окажется что-то другое.
    /// </summary>
    /// <param name="name">Чей лог — для имени файла: вкладка или сервер.</param>
    /// <param name="auto">Автокопия; кнопкой — CopyPass не взводится, как и у текста.</param>
    /// <returns>Имя файла, если он в буфере; null — не вышло, причина в журнале.</returns>
    public static string? CopyTerminalOutputFile(string text, string name, Workspace tab, bool auto = true)
    {
        string path;

        try
        {
            Directory.CreateDirectory(LogDir);
            var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
            path = Path.Combine(LogDir, $"лог {safe} {DateTime.Now:dd.MM HH-mm-ss}.txt");

            // с BOM: Блокнот и чаты без него иногда читали кириллицу не той кодировкой
            File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch (Exception ex)
        {
            AppLog.Hint("Не удалось сохранить лог в файл: " + ex.Message, LogLevel.Error);
            return null;
        }

        var data = new DataObject();
        data.SetFileDropList(new StringCollection { path });

        var landed = () => ReadFiles(1) is { Count: 1 } files
            && string.Equals(files[0], path, StringComparison.OrdinalIgnoreCase);

        if (!Put(data, landed, "Не удалось положить файл в буфер обмена"))
        {
            TryDelete(path);
            return null;
        }

        // прежний файл больше не в буфере — его место занял этот
        if (_logFile is { } previous && previous != path)
        {
            TryDelete(previous);
        }

        _logFile = path;
        _terminalOutput = null;
        _terminalOutputAuto = auto;

        if (auto)
        {
            CopyPass.Arm(tab);
        }
        else
        {
            CopyPass.Disarm();
        }

        return Path.GetFileName(path);
    }

    /// <summary>В буфере — наш файл с логом, и только он.</summary>
    public static bool IsOurLogFile(StringCollection? files) =>
        _logFile is { } file && files is { Count: 1 } && string.Equals(files[0], file, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Следить за буфером: сменился — файл лога, что там лежал, удаляется. Заодно убирает файлы,
    /// оставшиеся с прошлого запуска.
    /// </summary>
    public static void Watch(Window window)
    {
        try
        {
            if (Directory.Exists(LogDir))
            {
                foreach (var old in Directory.EnumerateFiles(LogDir, "*.txt"))
                {
                    TryDelete(old);
                }
            }
        }
        catch (Exception)
        {
            // папку держит кто-то ещё — уберём в следующий раз
        }

        if (PresentationSource.FromVisual(window) is not HwndSource source)
        {
            return;
        }

        // окно может быть уже подписано (поле команды следит за буфером само) — тогда вызов просто не нужен
        AddClipboardFormatListener(source.Handle);
        source.AddHook(OnWindowMessage);
    }

    /// <summary>Программа закрывается: файл, которого в буфере уже нет, удаляется.</summary>
    public static void Release() => DropIfReplaced();

    private const int WmClipboardUpdate = 0x031D;

    private static IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate)
        {
            DropIfReplaced();
        }

        return IntPtr.Zero;
    }

    private static void DropIfReplaced()
    {
        if (_logFile is not { } file)
        {
            return;
        }

        try
        {
            if (HasFiles && IsOurLogFile(ReadFiles(1)))
            {
                return;
            }
        }
        catch (Exception)
        {
            // буфер занят другой программой — проверим на следующей смене
            return;
        }

        _logFile = null;
        TryDelete(file);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // файл ещё читает чат — уберётся при следующем запуске
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    /// <summary>В буфере — тот самый вывод терминала, что туда положили мы последним.</summary>
    public static bool IsTerminalOutput(string? text) =>
        text is not null && _terminalOutput is not null && Normalize(text) == _terminalOutput;

    /// <summary>
    /// В буфере — вывод терминала, который туда положили мы. Возвращает предупреждение
    /// для плашки или null, если это что-то другое.
    /// </summary>
    public static string? TerminalOutputWarning(string text)
    {
        if (_terminalOutput is null || Normalize(text) != _terminalOutput)
        {
            return null;
        }

        return _terminalOutputAuto
            ? "Не вставлено: в буфере обмена вывод из журнала, скопированный автоматически. Скопируйте команду заново."
            : "Не вставлено: в буфере обмена вывод из журнала, а не команда. Скопируйте команду заново.";
    }

    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
}
