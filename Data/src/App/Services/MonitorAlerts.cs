using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MultiServerSync.Models;
using MultiServerSync.Views.Controls;
using MultiServerSync.Views.Dialogs;

namespace MultiServerSync.Services;

/// <summary>
/// Сигналы «Мониторинга»: у раздела сервера (процессор, память, диск) включён колокольчик — раздел покраснел,
/// и один раз звучит сигнал, вынесенное окно этого сервера выходит вперёд (нет окна — открывается), а раздел
/// в нём — в тускло-оранжевой рамке. Рамка гаснет через 3 с после первого любого действия человека в окнах
/// программы: щелчок, клавиша, колесо, перенос, сворачивание, закрытие.
/// Слежка — по фоновому опросу (<see cref="MonitorWarmup"/>), открытые окна для неё не нужны.
/// Включённые сигналы — в настройках (<see cref="AppSettings.MonitorAlerts"/>), по умолчанию у всех выключены.
/// </summary>
public static class MonitorAlerts
{
    /// <summary>Красный — «перегружен», «почти кончилась», «место кончается»: те же пороги, что у статуса карточки.</summary>
    public const double CpuBad = 90;
    public const double MemBad = 90;
    public const double DiskBad = 90;

    public static readonly string[] Keys = ["cpu", "mem", "disk"];

    /// <summary>Рамка гаснет через столько после первого действия человека.</summary>
    private static readonly TimeSpan FadeAfter = TimeSpan.FromSeconds(3);

    /// <summary>Своё окно вперёд (разворот, перенос, открытие) — не действие человека: столько после сигнала не считаем.</summary>
    private static readonly TimeSpan OwnMoves = TimeSpan.FromSeconds(1);

    private static readonly Dictionary<string, (MonitorFeed Feed, Action Handler)> Followed = new(StringComparer.Ordinal);

    /// <summary>Был ли раздел красным на прошлом замере: сигнал — только на переходе в красный.</summary>
    private static readonly Dictionary<string, bool> WasRed = new(StringComparer.Ordinal);

    private static readonly List<Light> Lights = [];
    private static DateTime _ownUntil;
    private static bool _started;

    /// <summary>Сигналы, включённые при запуске программы: их первый замер красным тоже срабатывает (24.11, «при старте смотреть, где красное»).</summary>
    private static readonly HashSet<string> AtStart = new(StringComparer.Ordinal);

    /// <summary>Сигнал включили или выключили — виды перерисовывают кнопки.</summary>
    public static event Action? Changed;

    public static string Name(string key) => key switch
    {
        "cpu" => "Процессор",
        "mem" => "Память",
        _ => "Диск",
    };

    private static string Id(ServerEntry server, string key) => server.Id + ":" + key;

    public static bool IsOn(ServerEntry server, string key) => AppState.Current.Settings.MonitorAlerts.Contains(Id(server, key));

    public static string Tip(string key, bool on) => on
        ? $"Сигнал включён: «{Name(key)}» покраснеет — прозвучит сигнал и окно сервера выйдет вперёд. Щелчок — выключить"
        : $"Сигнал выключен. Щелчок — включить: «{Name(key)}» покраснеет — прозвучит сигнал и окно сервера выйдет вперёд";

    /// <summary>С запуска: следить за серверами с включёнными сигналами и ловить действия человека для рамки.</summary>
    public static void Start(Window main)
    {
        if (_started)
        {
            return;
        }

        _started = true;

        // любое нажатие, клавиша или колесо в любом окне программы
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler((_, _) => Touched()), true);
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewKeyDownEvent, new KeyEventHandler((_, _) => Touched()), true);
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler((_, _) => Touched()), true);

        // перенос за заголовок и сворачивание идут мимо мыши WPF — по событиям окна
        main.LocationChanged += (_, _) => Touched();
        main.StateChanged += (_, _) => Touched();

        AppState.Current.Servers.CollectionChanged += OnServersChanged;

        // у «Диска» выбрали другое хранилище — его состояние с чистого листа: выбор сам по себе сигнала не даёт
        MonitorDisks.Changed += server => WasRed.Remove(Id(server, "disk"));
        AtStart.UnionWith(AppState.Current.Settings.MonitorAlerts);
        Sync();
    }

    public static void Toggle(ServerEntry server, string key)
    {
        var list = AppState.Current.Settings.MonitorAlerts;
        var id = Id(server, key);
        var on = !list.Remove(id);

        if (on)
        {
            list.Add(id);
        }
        else
        {
            AtStart.Remove(id);
        }

        AppState.Current.SaveSettings();
        Sync();
        // настройка программы, а не действие на сервере: строка «Программы» — пишется, только если её галочки отмечены
        ActionLog.Info($"Мониторинг: сигнал «{Name(key)}» у «{server.PaneLabel}» {(on ? "включён" : "выключен")}", null, AppSection.Monitor,
            AppState.Current.MonitorTabOf(server), on ? [$"раздел покраснеет — прозвучит сигнал и окно сервера выйдет вперёд"] : null);
        Changed?.Invoke();
    }

    private static void OnServersChanged(object? sender, NotifyCollectionChangedEventArgs e) => Sync();

    /// <summary>Подписка на серверы с включёнными сигналами; удалённых серверов сигналы убираются из настроек.</summary>
    private static void Sync()
    {
        var servers = AppState.Current.Servers.ToList();
        var list = AppState.Current.Settings.MonitorAlerts;
        var ids = servers.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);

        if (list.RemoveAll(a => !ids.Contains(a.Split(':')[0])) > 0)
        {
            AppState.Current.SaveSettings();
        }

        var want = servers
            .Where(s => Keys.Any(k => IsOn(s, k)))
            .GroupBy(s => s.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var (id, (feed, handler)) in Followed.ToList())
        {
            if (!want.TryGetValue(id, out var server) || !ReferenceEquals(server, feed.Server))
            {
                feed.Changed -= handler;
                Followed.Remove(id);
            }
        }

        foreach (var (id, server) in want)
        {
            if (Followed.ContainsKey(id))
            {
                Check(server, Followed[id].Feed);
                continue;
            }

            var feed = MonitorFeed.For(server);
            Action handler = () => Check(server, feed);
            feed.Changed += handler;
            Followed[id] = (feed, handler);
            Check(server, feed);
        }

        // выключенные — без памяти о прошлом: включили уже красным — сигнал только на следующем покраснении
        foreach (var key in WasRed.Keys.ToList())
        {
            var parts = key.Split(':');

            if (servers.FirstOrDefault(s => s.Id == parts[0]) is not { } server || !IsOn(server, parts[1]))
            {
                WasRed.Remove(key);
            }
        }
    }

    private static void Check(ServerEntry server, MonitorFeed feed)
    {
        // нет связи — ни красного, ни нормы: состояние не меняется, возврат связи сигнала не даёт
        if (feed.Trouble is not null || feed.Now is not { } now)
        {
            return;
        }

        foreach (var key in Keys)
        {
            var id = Id(server, key);

            if (!IsOn(server, key))
            {
                WasRed.Remove(id);
                continue;
            }

            var red = key switch
            {
                "cpu" => now.Cpu >= CpuBad,
                "mem" => now.MemTotal > 0 && now.MemUsed * 100.0 / now.MemTotal >= MemBad,
                // как статус карточки: по самому заполненному записываемому разделу выбранного носителя
                _ => MonitorDisks.Pick(server, feed.Info?.Disks)?.Worst is { } fs && fs.Pct >= DiskBad,
            };

            var known = WasRed.TryGetValue(id, out var was);
            WasRed[id] = red;

            // сигнал включён с прошлого запуска — первый замер после старта: уже красный раздел тоже показывается
            var startup = !known && AtStart.Remove(id);

            if ((known && !was || startup) && red)
            {
                Fire(server, key, now, feed);
            }
        }
    }

    private static void Fire(ServerEntry server, string key, MonitorSample now, MonitorFeed feed)
    {
        var what = key switch
        {
            "cpu" => $"перегружен, {MonitorFormat.Pct(now.Cpu)}",
            "mem" => $"почти кончилась, занято {MonitorFormat.Pct(now.MemUsed * 100.0 / now.MemTotal)}",
            _ => MonitorDisks.Pick(server, feed.Info?.Disks) is { Worst: { } fs } disk
                ? $"место кончается: {disk.Label}, {fs.Mount} — свободно {MonitorFormat.Bytes(fs.Avail)}"
                : "место кончается",
        };

        AppLog.Debug($"Сигнал мониторинга: «{server.PaneLabel}» · {Name(key)} — {what}.");
        ActionLog.Warn($"Мониторинг: {Name(key)} — {what}", server, AppSection.Monitor, AppState.Current.MonitorTabOf(server),
            ["сработал сигнал: окно сервера выведено вперёд"]);

        FinishSound.Alert();
        _ownUntil = DateTime.UtcNow + OwnMoves;
        MonitorWindow.Alert(server, key);
    }

    // ── рамка ────────────────────────────────────────────────────────────────────

    private sealed class Light
    {
        public required MonitorCard Card { get; init; }

        public DispatcherTimer? Timer { get; set; }
    }

    /// <summary>Зажечь рамку раздела; горела — снова ждёт действия человека.</summary>
    public static void LightUp(MonitorCard card)
    {
        _ownUntil = DateTime.UtcNow + OwnMoves;

        if (Lights.FirstOrDefault(l => ReferenceEquals(l.Card, card)) is { } old)
        {
            old.Timer?.Stop();
            old.Timer = null;
        }
        else
        {
            Lights.Add(new Light { Card = card });
        }

        card.Lit = true;
    }

    /// <summary>Погасить сразу: вид переключили на другой сервер.</summary>
    public static void Off(MonitorCard card)
    {
        if (Lights.FirstOrDefault(l => ReferenceEquals(l.Card, card)) is { } light)
        {
            light.Timer?.Stop();
            Lights.Remove(light);
        }

        card.Lit = false;
    }

    /// <summary>Человек что-то сделал в окнах программы — горящие рамки гаснут через 3 с.</summary>
    public static void Touched()
    {
        if (Lights.Count == 0 || DateTime.UtcNow < _ownUntil)
        {
            return;
        }

        foreach (var light in Lights.Where(l => l.Timer is null).ToList())
        {
            var timer = new DispatcherTimer { Interval = FadeAfter };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                light.Card.Lit = false;
                Lights.Remove(light);
            };
            light.Timer = timer;
            timer.Start();
        }
    }
}
