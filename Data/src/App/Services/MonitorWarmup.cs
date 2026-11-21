using System.Collections.Specialized;
using MultiServerSync.Models;

namespace MultiServerSync.Services;

/// <summary>
/// «Мониторинг» заранее: с запуска программы каждый сервер опрашивается в фоне и его история читается
/// на самый длинный период — раздел и вынесенные окна открываются сразу с графиками, а не ждут минутами.
/// Сервер добавили — читается и он, удалили — опрос снят.
/// </summary>
public static class MonitorWarmup
{
    /// <summary>Сначала даём программе открыться.</summary>
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(5);

    /// <summary>Между серверами — чтобы входы не шли все в одну секунду.</summary>
    private static readonly TimeSpan Stagger = TimeSpan.FromMilliseconds(700);

    private static readonly Dictionary<string, MonitorFeed> Watched = new(StringComparer.Ordinal);
    private static bool _started;

    public static async void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;

        await Task.Delay(StartDelay);

        AppState.Current.Servers.CollectionChanged += OnServersChanged;
        await SyncAsync(stagger: true);
    }

    private static void OnServersChanged(object? sender, NotifyCollectionChangedEventArgs e) => _ = SyncAsync(stagger: false);

    private static async Task SyncAsync(bool stagger)
    {
        var servers = AppState.Current.Servers.ToList();
        var byId = servers.GroupBy(s => s.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // удалённые и заменённые (запись сервера сменилась — у неё свой источник) — опрос снят
        foreach (var (id, feed) in Watched.ToList())
        {
            if (!byId.TryGetValue(id, out var now) || !ReferenceEquals(now, feed.Server))
            {
                feed.Unwatch(MonitorFeed.Background);
                Watched.Remove(id);
            }
        }

        foreach (var server in servers)
        {
            if (Watched.ContainsKey(server.Id))
            {
                continue;
            }

            var feed = MonitorFeed.For(server);
            Watched[server.Id] = feed;
            feed.Watch(MonitorFeed.Background, MonitorRange.Quarter);

            if (stagger)
            {
                await Task.Delay(Stagger);
            }
        }
    }
}
