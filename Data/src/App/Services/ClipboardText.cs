using System.Windows;

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
    public static bool CopyTerminalOutput(string text, bool auto = false)
    {
        if (!Copy(text))
        {
            return false;
        }

        _terminalOutput = Normalize(text);
        _terminalOutputAuto = auto;
        return true;
    }

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
