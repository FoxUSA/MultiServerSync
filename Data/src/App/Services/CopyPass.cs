using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;

namespace MultiServerSync.Services;

/// <summary>
/// CopyPass: лог, который программа положила в буфер обмена, сам вставляется в первое поле
/// ввода другой программы, куда перейдёт фокус, и уходит нажатием Enter. Один раз на копию.
/// Перед вставкой буфер сверяется: если там уже не наш лог, ничего не делается и ожидание
/// снимается. Терминалы пропускаются — там Enter выполнил бы строки лога как команды; поля
/// паролей тоже. За фокусом следит UI Automation — только пока лог ждёт.
/// </summary>
public static class CopyPass
{
    private static readonly Lock Sync = new();

    /// <summary>Лог ждёт поля ввода.</summary>
    private static bool _armed;

    private static AutomationFocusChangedEventHandler? _handler;

    /// <summary>Вставка идёт: фокус, прыгнувший от неё же, второй раз не срабатывает.</summary>
    private static int _busy;

    /// <summary>Программы-терминалы: Enter там выполнил бы лог строками команд.</summary>
    private static readonly HashSet<string> Terminals = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal", "OpenConsole", "conhost", "cmd", "powershell", "pwsh",
        "putty", "kitty", "mintty", "wezterm-gui", "alacritty", "MobaXterm", "Tabby", "Termius", "WinSCP",
    };

    /// <summary>Лог скопирован — ждать поля ввода, если галочка CopyPass стоит.</summary>
    /// <param name="auto">
    /// Скопировано галочкой «авто». Операция могла закончиться в другом разделе, где «авто» своя,
    /// поэтому смотрится сама галочка, а не её вид в открытом разделе.
    /// </param>
    public static void Arm(bool auto = false)
    {
        if (!(auto ? AppState.Current.Settings.CopyPass : AppState.Current.CopyPass))
        {
            return;
        }

        lock (Sync)
        {
            _armed = true;
        }

        // без своей плашки: следом плашка «Вывод скопирован», она и так перекроет
        Subscribe();
    }

    /// <summary>Больше не ждать: сработало, буфер сменился или галочку сняли.</summary>
    public static void Disarm()
    {
        lock (Sync)
        {
            _armed = false;
        }

        Unsubscribe();
    }

    // подписку UI Automation не делают из потока окна: события приходят в своём потоке
    private static void Subscribe() => Task.Run(() =>
    {
        AutomationFocusChangedEventHandler? added = null;

        lock (Sync)
        {
            if (!_armed)
            {
                return;
            }

            // уже следим — прошлая копия ещё ждала; новой нужна только проверка курсора ниже
            if (_handler is null)
            {
                _handler = added = OnFocus;
            }
        }

        if (added is not null)
        {
            try
            {
                Automation.AddAutomationFocusChangedEventHandler(added);
            }
            catch (Exception ex)
            {
                lock (Sync)
                {
                    _handler = null;
                }

                AppLog.Hint("CopyPass не смог следить за полями ввода: " + ex.Message, LogLevel.Warning);
                return;
            }
        }

        // курсор уже стоит в поле другой программы (ждали там, пока команда кончится) —
        // фокус никуда не перейдёт, поэтому вставка сразу
        if (Focused() is { } current)
        {
            Consider(current);
        }
    });

    private static AutomationElement? Focused()
    {
        try
        {
            return AutomationElement.FocusedElement;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Unsubscribe() => Task.Run(() =>
    {
        AutomationFocusChangedEventHandler? handler;

        lock (Sync)
        {
            handler = _handler;
            _handler = null;
        }

        if (handler is not null)
        {
            try
            {
                Automation.RemoveAutomationFocusChangedEventHandler(handler);
            }
            catch (Exception ex)
            {
                AppLog.Debug("CopyPass: снять слежку за фокусом не вышло: " + ex.Message);
            }
        }
    });

    private static void OnFocus(object sender, AutomationFocusChangedEventArgs e)
    {
        lock (Sync)
        {
            if (!_armed)
            {
                return;
            }
        }

        if (sender is AutomationElement element)
        {
            Consider(element);
        }
    }

    private enum Target
    {
        /// <summary>Поле ввода другой программы — сюда.</summary>
        Input,

        /// <summary>Точно не сюда: своё окно, пароль, терминал, не поле.</summary>
        Never,

        /// <summary>Панель Electron-программы, ещё не открывшей свои элементы: переспросить.</summary>
        Unclear,
    }

    /// <summary>
    /// Фокус пришёл на элемент: поле — вставить. При переходе в окно фокус сначала приходит на само
    /// окно (панель), а уже потом на поле; Electron-программы (VS Code, Claude, Discord) к тому же
    /// описывают свои поля только после первого обращения. Поэтому на панели фокус переспрашивается
    /// часто и недолго: событие о поле в это время отброшено как повтор, ждать его нельзя.
    /// </summary>
    private static void Consider(AutomationElement element)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        try
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                lock (Sync)
                {
                    if (!_armed)
                    {
                        return;
                    }
                }

                switch (Classify(element))
                {
                    case Target.Input:
                        Pass();
                        return;

                    case Target.Never:
                        return;
                }

                Thread.Sleep(50);

                if (Focused() is not { } again)
                {
                    return;
                }

                element = again;
            }
        }
        finally
        {
            _busy = 0;
        }
    }

    /// <summary>Поле, куда можно писать, — в чужой программе, не терминал и не пароль.</summary>
    private static Target Classify(AutomationElement element)
    {
        try
        {
            var info = element.Current;

            if (info.ProcessId == Environment.ProcessId || info.IsPassword || !info.IsEnabled || IsTerminal(info.ProcessId))
            {
                return Target.Never;
            }

            if (info.ControlType == ControlType.Pane)
            {
                return Target.Unclear;
            }

            if (info.ControlType != ControlType.Edit && info.ControlType != ControlType.Document)
            {
                return Target.Never;
            }

            // у документа без ValuePattern не понять, можно ли в нём писать (страница браузера) — мимо
            var editable = element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)
                ? !((ValuePattern)pattern).Current.IsReadOnly
                : info.ControlType == ControlType.Edit;

            return editable ? Target.Input : Target.Never;
        }
        catch (Exception)
        {
            // элемент исчез, пока его спрашивали, — не поле
            return Target.Never;
        }
    }

    private static bool IsTerminal(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return Terminals.Contains(process.ProcessName);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void Pass()
    {
        // Alt+Tab ещё держат: Ctrl+V вместе с Alt стал бы другой комбинацией — ждём, пока отпустят
        if (!WaitKeysUp(TimeSpan.FromSeconds(3)))
        {
            return;
        }

        if (!ClipboardHoldsOurLog())
        {
            Disarm();
            AppLog.Hint("CopyPass: в буфере уже не лог программы — вставлять не стал.");
            return;
        }

        Disarm();

        // Enter — после того как поле приняло вставку, иначе уйдёт пустое сообщение
        Press(VkControl, VkV);
        Thread.Sleep(80);
        Press(VkReturn);

        AppLog.Hint("CopyPass: лог вставлен и отправлен.");
    }

    private static bool ClipboardHoldsOurLog()
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null)
        {
            return false;
        }

        // буфер читается только из потока окна
        var text = dispatcher.Invoke(() =>
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch (Exception)
            {
                return null;
            }
        });

        return ClipboardText.IsTerminalOutput(text);
    }

    private const byte VkShift = 0x10;
    private const byte VkControl = 0x11;
    private const byte VkMenu = 0x12;
    private const byte VkLWin = 0x5B;
    private const byte VkRWin = 0x5C;
    private const byte VkV = 0x56;
    private const byte VkReturn = 0x0D;
    private const uint KeyUp = 0x0002;

    private static bool WaitKeysUp(TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;

        while (new[] { VkShift, VkControl, VkMenu, VkLWin, VkRWin }.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0))
        {
            if (DateTime.UtcNow > until)
            {
                return false;
            }

            Thread.Sleep(30);
        }

        return true;
    }

    /// <summary>Нажать сочетание: клавиши вниз по порядку, вверх — в обратном.</summary>
    private static void Press(params byte[] keys)
    {
        foreach (var key in keys)
        {
            keybd_event(key, 0, 0, UIntPtr.Zero);
        }

        foreach (var key in Enumerable.Reverse(keys))
        {
            keybd_event(key, 0, KeyUp, UIntPtr.Zero);
        }
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);
}
