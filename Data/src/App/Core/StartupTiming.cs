using System.Diagnostics;
using System.Text;
using MultiServerSync.Services;

namespace MultiServerSync.Core;

/// <summary>
/// Замер запуска по этапам — одной отладочной строкой в журнал (только в файл). Отсчёт от
/// нажатия на ярлык: лаунчер передаёт момент своего старта в <see cref="LauncherStartVariable"/>,
/// без лаунчера — от старта самого процесса программы.
/// </summary>
public sealed class StartupTiming
{
    /// <summary>Переменная окружения, в которой лаунчер передаёт момент своего старта (UTC, тики).</summary>
    public const string LauncherStartVariable = "MSS_LAUNCHER_START";

    private readonly DateTime _origin;
    private readonly DateTime _processStart;
    private readonly bool _fromLauncher;
    private readonly StringBuilder _steps = new();
    private DateTime _last;

    private StartupTiming()
    {
        _processStart = Process.GetCurrentProcess().StartTime.ToUniversalTime();

        _fromLauncher = long.TryParse(Environment.GetEnvironmentVariable(LauncherStartVariable), out var ticks)
            && ticks > 0
            && ticks <= _processStart.Ticks;

        _origin = _fromLauncher ? new DateTime(ticks, DateTimeKind.Utc) : _processStart;

        if (_fromLauncher)
        {
            _steps.Append($"лаунчер {Ms(_origin, _processStart)}");
        }

        _last = _processStart;
        Mark("запуск .NET и WPF");
    }

    public static StartupTiming Begin() => new();

    public void Mark(string step)
    {
        var now = DateTime.UtcNow;
        _steps.Append(_steps.Length > 0 ? " · " : string.Empty).Append($"{step} {Ms(_last, now)}");
        _last = now;
    }

    /// <summary>Последний этап — и строка в журнал. Повторно не пишет.</summary>
    public void Finish(string step)
    {
        if (_last == DateTime.MinValue)
        {
            return;
        }

        Mark(step);
        AppLog.Debug($"Запуск за {Ms(_origin, _last)} мс ({(_fromLauncher ? "от лаунчера" : "от старта программы")}): {_steps}.");
        _last = DateTime.MinValue;
    }

    private static string Ms(DateTime from, DateTime to) => $"{(to - from).TotalMilliseconds:0}";
}
