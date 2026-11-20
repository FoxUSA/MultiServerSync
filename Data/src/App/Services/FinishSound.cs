using System.IO;
using System.Windows;
using System.Windows.Media;

namespace MultiServerSync.Services;

/// <summary>
/// Звук об окончании команды или копирования — и на открытой вкладке (с 14.11). Чтобы не частил:
/// после звука 3 секунды тишины (что закончилось в них — один повтор в конце), мгновенный запуск
/// на вкладке перед глазами молчит, остановленный человеком — тоже. Громкость — в «Настройках», 0% выключает.
/// </summary>
public static class FinishSound
{
    private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "Assets", "yet.mp3");

    /// <summary>
    /// Играющие плееры — до конца звука: локальный собирается сборщиком мусора и замолкает на полуслове.
    /// Плеер новый на каждый звук: один на всё время работы однажды замолкал насовсем (после сна или смены
    /// устройства вывода он остаётся при прежнем), и звук возвращал только перезапуск программы.
    /// </summary>
    private static readonly HashSet<MediaPlayer> Playing = [];

    /// <summary>После звука — тишина: окончания в эти секунды копятся в один повтор в её конце.</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(3);

    /// <summary>Запуск короче этого на открытой вкладке молчит: итог появился, пока на него смотрели.</summary>
    private static readonly TimeSpan Instant = TimeSpan.FromSeconds(3);

    private static DateTime _quietUntil;
    private static bool _owed;
    private static System.Windows.Threading.DispatcherTimer? _timer;

    public static void OnFinished(OperationBatch batch)
    {
        // плеер и таймер живут в потоке окна, а прогон мог закончиться в фоне
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => OnFinished(batch)));
            return;
        }

        // только прогоны вкладок, не служебные
        if (AppState.Current.OwnerOf(batch) is not { } owner)
        {
            return;
        }

        // «Остановить» нажал сам человек — сообщать не о чем
        if (batch.RunStopped > 0)
        {
            AppLog.Debug($"Звук: молчит — «{owner.Title}» остановлена вручную.");
            return;
        }

        // мгновенная команда на вкладке перед глазами — итог и так виден
        if (batch.StartedAt is { } started && DateTime.Now - started < Instant && IsInView(owner))
        {
            AppLog.Debug($"Звук: молчит — «{owner.Title}» шла {(DateTime.Now - started).TotalSeconds:0.0} с на открытой вкладке.");
            return;
        }

        if (DateTime.UtcNow < _quietUntil)
        {
            AppLog.Debug($"Звук: «{owner.Title}» — повтор после паузы.");
            _owed = true;
            return;
        }

        AppLog.Debug($"Звук: «{owner.Title}» закончилась" + (batch.StartedAt is { } at ? $" за {(DateTime.Now - at).TotalSeconds:0.0} с." : "."));
        Ring();
    }

    /// <summary>Звук и пауза за ним; что закончилось в паузу — один повтор в её конце.</summary>
    private static void Ring()
    {
        Play();
        _quietUntil = DateTime.UtcNow + Quiet;

        _timer ??= new System.Windows.Threading.DispatcherTimer { Interval = Quiet };
        _timer.Tick -= OnQuietOver;
        _timer.Tick += OnQuietOver;
        _timer.Stop();
        _timer.Start();
    }

    private static void OnQuietOver(object? sender, EventArgs e)
    {
        _timer?.Stop();

        if (_owed)
        {
            _owed = false;
            Ring();
        }
    }

    /// <summary>Вкладка открыта в своём разделе, а окно на экране и в фокусе.</summary>
    private static bool IsInView(Workspace owner)
    {
        var state = AppState.Current;
        var window = Application.Current?.MainWindow;

        return window is { IsActive: true }
            && window.WindowState != WindowState.Minimized
            && state.ShowsWorkspaceTabs
            && state.Tabs.Contains(owner)
            && ReferenceEquals(state.ActiveWorkspace, owner);
    }

    /// <summary>Проба после выбора громкости в «Настройках» — играет всегда, кроме 0%.</summary>
    public static void Preview() => Play();


    private static void Play()
    {
        var volume = Math.Clamp(AppState.Current.Settings.FinishSoundVolume, 0, 100);

        if (volume == 0 || !File.Exists(FilePath))
        {
            AppLog.Debug(volume == 0 ? "Звук: громкость 0% — выключен." : "Звук: нет файла " + FilePath);
            return;
        }

        try
        {
            var player = new MediaPlayer();
            Playing.Add(player);

            void Done()
            {
                if (Playing.Remove(player))
                {
                    player.Close();
                }
            }

            player.MediaOpened += (_, _) => AppLog.Debug($"Звук: файл открыт, длина {player.NaturalDuration}, громкость {player.Volume:P0}.");
            player.MediaEnded += (_, _) =>
            {
                AppLog.Debug("Звук: доиграл.");
                Done();
            };

            // сбой плеер не бросает исключением, а сообщает этим событием — без него звук пропадал молча
            player.MediaFailed += (_, e) =>
            {
                AppLog.Debug("Звук окончания не проигрался: " + (e.ErrorException?.Message ?? "плеер не открыл файл"));
                Done();
            };

            player.Open(new Uri(FilePath, UriKind.Absolute));
            player.Volume = volume / 100.0;
            player.Play();

            // MediaEnded не пришёл (устройство пропало посреди звука) — плеер всё равно убирается
            var guard = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            guard.Tick += (_, _) =>
            {
                guard.Stop();

                if (Playing.Contains(player))
                {
                    AppLog.Debug("Звук: за 15 с не доиграл и не сообщил об ошибке — плеер закрыт.");
                }

                Done();
            };
            guard.Start();
        }
        catch (Exception ex)
        {
            // без звука работа не страдает — только отметка в файле журнала
            AppLog.Debug("Звук окончания не проигрался: " + ex.Message);
        }
    }
}
