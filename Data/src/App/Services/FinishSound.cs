using System.IO;
using System.Windows;
using System.Windows.Media;

namespace MultiServerSync.Services;

/// <summary>
/// Тихий щелчок об окончании команды или копирования — только когда итог не перед глазами:
/// окно свёрнуто или в фоне, либо закончилась не та вкладка, что открыта сейчас.
/// Если человек смотрит прямо на эту вкладку, звук лишний — итог и так виден.
/// </summary>
public static class FinishSound
{
    private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "Assets", "send.mp3");

    /// <summary>Плеер держим полем: локальный собирается сборщиком мусора и замолкает на полуслове.</summary>
    private static MediaPlayer? _player;

    public static void OnFinished(OperationBatch batch)
    {
        var state = AppState.Current;

        if (state.OwnerOf(batch) is not { } owner || IsInView(owner))
        {
            return;
        }

        Play();
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

    private static void Play()
    {
        if (!File.Exists(FilePath))
        {
            return;
        }

        try
        {
            _player ??= new MediaPlayer();
            _player.Open(new Uri(FilePath, UriKind.Absolute));
            _player.Volume = 0.6;
            _player.Play();
        }
        catch (Exception ex)
        {
            // без звука работа не страдает — только отметка в файле журнала
            AppLog.Debug("Звук окончания не проигрался: " + ex.Message);
        }
    }
}
