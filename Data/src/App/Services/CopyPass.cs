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

    /// <summary>
    /// Сколько лог ждёт поля ввода, считая от попадания в буфер: хватает перейти в нужную программу.
    /// Дольше — вы уже заняты другим, и вставка в случайное поле была бы сюрпризом.
    /// </summary>
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    /// <summary>Номер копии: срок снимает ожидание, только если новой копии за это время не было.</summary>
    private static int _generation;

    private static AutomationFocusChangedEventHandler? _handler;

    /// <summary>Вставка идёт: фокус, прыгнувший от неё же, второй раз не срабатывает.</summary>
    private static int _busy;

    /// <summary>Программы-терминалы: Enter там выполнил бы лог строками команд.</summary>
    private static readonly HashSet<string> Terminals = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal", "OpenConsole", "conhost", "cmd", "powershell", "pwsh",
        "putty", "kitty", "mintty", "wezterm-gui", "alacritty", "MobaXterm", "Tabby", "Termius", "WinSCP",
    };

    /// <summary>Лог скопирован галочкой «авто» — ждать поля ввода, если CopyPass стоит.</summary>
    /// <param name="tab">
    /// Вкладка операции. Она могла закончиться, пока открыта другая, поэтому смотрится CopyPass её вкладки.
    /// </param>
    public static void Arm(Workspace tab)
    {
        if (!AppState.Current.CopyPassOf(tab))
        {
            AppLog.Debug($"CopyPass: не взведена — у вкладки «{tab.Title}» галочки нет.");
            return;
        }

        var once = tab.CopyPassOnce;

        // разовая (правый щелчок) уходит на эту копию
        AppState.Current.UseCopyPassOnce(tab);

        int generation;
        bool wasArmed;

        lock (Sync)
        {
            wasArmed = _armed;
            _armed = true;
            generation = ++_generation;
        }

        AppLog.Debug($"CopyPass: взведена №{generation} · вкладка «{tab.Title}» · {(once ? "разовая" : "постоянная")}"
            + (wasArmed ? " · прошлая ещё ждала" : string.Empty) + (_busy == 1 ? " · идёт вставка прошлой" : string.Empty));

        // без своей плашки: об окончании уже сказал итог, о вставке скажет Pass
        Subscribe();
        _ = ExpireAsync(generation);
    }

    /// <summary>Срок вышел, а лог так никуда и не вставлен — больше не ждать.</summary>
    private static async Task ExpireAsync(int generation)
    {
        await Task.Delay(WaitLimit).ConfigureAwait(false);

        lock (Sync)
        {
            if (!_armed || _generation != generation)
            {
                return;
            }

            _armed = false;
        }

        Unsubscribe();
        AppLog.Hint($"CopyPass: за {WaitLimit.TotalSeconds:0} с поле ввода не нашлось — лог больше не ждёт.");
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
        // курсор уже стоит в поле другой программы (ждали там, пока команда кончится) —
        // фокус никуда не перейдёт, поэтому вставка сразу. Это раньше подписки: регистрация
        // в UI Automation при первом обращении идёт секунду-две, а ждать её тут незачем
        if (Focused() is { } current)
        {
            Consider(current);
        }

        AutomationFocusChangedEventHandler? added = null;

        lock (Sync)
        {
            if (!_armed)
            {
                return;
            }

            // уже следим — прошлая копия ещё ждала; новой хватило проверки курсора выше
            if (_handler is null)
            {
                _handler = added = OnFocus;
            }
        }

        if (added is null)
        {
            return;
        }

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

        // пока подписывались, фокус мог успеть перейти в поле — событие об этом уже упущено
        if (Focused() is { } late)
        {
            Consider(late);
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
            AppLog.Debug("CopyPass: фокус пропущен — идёт прошлая вставка.");
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

                var target = Classify(element);

                if (target != Target.Unclear || attempt == 0)
                {
                    AppLog.Debug($"CopyPass: фокус — {Describe(element)} → {target}" + (attempt > 0 ? $" (попытка {attempt + 1})" : string.Empty));
                }

                switch (target)
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

    /// <summary>Для лога: чей элемент и что это.</summary>
    private static string Describe(AutomationElement element)
    {
        try
        {
            var info = element.Current;
            return $"{ProcessName(info.ProcessId)} · {info.ControlType.ProgrammaticName} · «{info.Name}»";
        }
        catch (Exception)
        {
            return "элемент исчез";
        }
    }

    private static string ProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception)
        {
            return $"процесс {processId}";
        }
    }

    private static string ForegroundName()
    {
        var window = GetForegroundWindow();
        GetWindowThreadProcessId(window, out var processId);
        return ProcessName((int)processId);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

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
            AppLog.Debug("CopyPass: Ctrl/Alt/Shift/Win держали дольше 3 с — не вставлял.");
            return;
        }

        if (ClipboardHoldsOurLog() is not { } asFile)
        {
            Disarm();
            AppLog.Hint("CopyPass: в буфере уже не лог программы — вставлять не стал.");
            return;
        }

        Disarm();

        var field = Focused();
        var before = TextLength(field);

        AppLog.Debug($"CopyPass: Ctrl+V → окно впереди: {ForegroundName()}, в поле символов: {Show(before)}{(asFile ? ", лог файлом" : string.Empty)}");
        Press(VkControl, VkV);

        // Enter — только когда поле приняло вставку: ChatGPT принимает её не сразу, и ранний Enter
        // уходил в пустоту — лог оставался в поле неотправленным
        int? pasted;

        if (asFile)
        {
            // вложение в поле не видно текстом — чату нужно время его принять
            Thread.Sleep(1500);
            pasted = TextLength(field);
        }
        else
        {
            pasted = WaitText(field, n => before is null || n > before, TimeSpan.FromSeconds(3));
            Thread.Sleep(pasted is null ? 300 : 120);
        }

        AppLog.Debug($"CopyPass: Enter → окно впереди: {ForegroundName()}, в поле символов: {Show(pasted ?? TextLength(field))}");
        Press(VkReturn);

        // ушло — поле опустело. Нет — Enter ещё раз, не больше двух
        for (var retry = 1; retry <= 2; retry++)
        {
            Thread.Sleep(500);
            var left = TextLength(field);

            if (left is null or 0 || (before is { } b && left <= b))
            {
                AppLog.Debug($"CopyPass: отправлено, в поле символов: {Show(left)}.");
                break;
            }

            if (Focused() is not { } now || now.Current.ProcessId != SafeProcessId(field))
            {
                AppLog.Debug("CopyPass: текст в поле остался, но фокус ушёл из окна — Enter не повторял.");
                break;
            }

            AppLog.Debug($"CopyPass: текст ещё в поле ({left} символов) — Enter повторно ({retry}).");
            Press(VkReturn);
        }

        AppLog.Echo("CopyPass: лог вставлен и отправлен.", LogLevel.Success);
    }

    /// <summary>В буфере наш лог: true — файлом, false — текстом; null — уже не наш.</summary>
    private static bool? ClipboardHoldsOurLog()
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null)
        {
            return null;
        }

        // буфер читается только из потока окна; длинный лог лежит там файлом (галочка «*.txt»)
        return dispatcher.Invoke<bool?>(() =>
        {
            try
            {
                if (ClipboardText.HasFiles)
                {
                    return ClipboardText.IsOurLogFile(ClipboardText.ReadFiles(1)) ? true : null;
                }

                return ClipboardText.IsTerminalOutput(ClipboardText.ReadText(1)) ? false : null;
            }
            catch (Exception)
            {
                return null;
            }
        });
    }

    /// <summary>Сколько символов в поле; null — поле своё содержимое не показывает.</summary>
    private static int? TextLength(AutomationElement? field)
    {
        try
        {
            if (field is not null && field.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            {
                return ((ValuePattern)pattern).Current.Value?.Length ?? 0;
            }

            if (field is not null && field.TryGetCurrentPattern(TextPattern.Pattern, out var text))
            {
                return ((TextPattern)text).DocumentRange.GetText(-1).TrimEnd('\r', '\n').Length;
            }
        }
        catch (Exception)
        {
            // поле исчезло или занято — не знаем
        }

        return null;
    }

    /// <summary>Ждать, пока длина текста в поле станет нужной; null — не дождались или не видно.</summary>
    private static int? WaitText(AutomationElement? field, Func<int, bool> ready, TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;

        while (DateTime.UtcNow < until)
        {
            if (TextLength(field) is not { } n)
            {
                return null;
            }

            if (ready(n))
            {
                return n;
            }

            Thread.Sleep(40);
        }

        AppLog.Debug($"CopyPass: за {limit.TotalSeconds:0} с вставка в поле не появилась.");
        return null;
    }

    private static string Show(int? length) => length?.ToString() ?? "не видно";

    private static int SafeProcessId(AutomationElement? element)
    {
        try
        {
            return element?.Current.ProcessId ?? -1;
        }
        catch (Exception)
        {
            return -1;
        }
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
