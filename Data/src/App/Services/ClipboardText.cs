using System.Collections.Specialized;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace MultiServerSync.Services;

/// <summary>
/// Копирование в буфер обмена. Буфер бывает занят другой программой, поэтому
/// одна повторная попытка — и в любом случае запись в журнал, а не молчание.
/// </summary>
public static class ClipboardText
{
    private static string? _terminalOutput;
    private static bool _terminalOutputAuto;

    public static bool Copy(string text)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                return true;
            }
            catch (Exception ex) when (attempt == 1)
            {
                AppLog.Debug("Буфер обмена занят, повтор: " + ex.Message);
                System.Threading.Thread.Sleep(120);
            }
            catch (Exception ex)
            {
                AppLog.Hint("Не удалось скопировать в буфер обмена: " + ex.Message, LogLevel.Error);
            }
        }

        return false;
    }

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
    /// <returns>Имя файла, если он в буфере; null — не вышло, причина в журнале.</returns>
    public static string? CopyTerminalOutputFile(string text, string name, Workspace tab)
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

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                break;
            }
            catch (Exception ex) when (attempt == 1)
            {
                AppLog.Debug("Буфер обмена занят, повтор: " + ex.Message);
                System.Threading.Thread.Sleep(120);
            }
            catch (Exception ex)
            {
                TryDelete(path);
                AppLog.Hint("Не удалось положить файл в буфер обмена: " + ex.Message, LogLevel.Error);
                return null;
            }
        }

        // прежний файл больше не в буфере — его место занял этот
        if (_logFile is { } previous && previous != path)
        {
            TryDelete(previous);
        }

        _logFile = path;
        _terminalOutput = null;
        _terminalOutputAuto = true;

        CopyPass.Arm(tab);
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
            if (Clipboard.ContainsFileDropList() && IsOurLogFile(Clipboard.GetFileDropList()))
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
            ? "Не вставлено: в буфере обмена вывод из терминала, скопированный автоматически. Скопируйте команду заново."
            : "Не вставлено: в буфере обмена вывод из терминала, а не команда. Скопируйте команду заново.";
    }

    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
}
