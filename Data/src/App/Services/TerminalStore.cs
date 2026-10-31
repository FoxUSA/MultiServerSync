using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows.Threading;
using MultiServerSync.Core;
using MultiServerSync.Models;

namespace MultiServerSync.Services;

/// <summary>
/// Терминал и журнал переживают перезапуск программы. Уходят они только корзиной или следующим
/// запуском после срока «Хранить лог» — как и без перезапуска. Пишется раз в несколько секунд,
/// если что-то поменялось, и при выходе: программу могут и снять через диспетчер задач.
/// </summary>
public static class TerminalStore
{
    /// <summary>Не чаще: UserData бывает в облачной папке, а файл с выводом большого копирования — мегабайты.</summary>
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Lock WriteSync = new();

    private static DispatcherTimer? _timer;

    private static volatile bool _dirty;

    private static bool _loaded;

    /// <summary>Что-то в терминале или журнале поменялось — записать при следующем тике.</summary>
    public static void MarkDirty() => _dirty = true;

    /// <summary>Вкладки загружены — вернуть их терминалы и журнал, дальше писать по таймеру.</summary>
    public static void Load()
    {
        var state = AppState.Current;
        var servers = state.Servers.ToDictionary(s => s.Id, StringComparer.Ordinal);
        // строки действий без сервера — «Компьютер», «Программа» — тоже возвращаются
        ServerEntry? Server(string id) => servers.GetValueOrDefault(id) ?? ActionLog.Pseudo(id);

        if (Read<TerminalFile>(AppPaths.TerminalFile) is { } file)
        {
            foreach (var workspace in state.AllWorkspaces)
            {
                if (file.Tabs.TryGetValue(workspace.Id, out var tab))
                {
                    workspace.RestoreTerminal(tab, Server, file.SavedAt);
                }
            }
        }

        if (Read<List<JournalEntryState>>(AppPaths.JournalFile) is { } journal)
        {
            AppLog.Restore(journal.Select(e => new LogEntry
            {
                Time = e.Time,
                Level = e.Level,
                Text = e.Text,
                Scope = e.Scope,
                Section = e.Section,
                IsServerLine = e.IsServerLine,
            }));
        }

        _loaded = true;
        _dirty = false;

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Period };
        _timer.Tick += (_, _) => Save(background: true);
        _timer.Start();
    }

    /// <summary>Записать, если было что. При выходе — сразу, в этом же потоке.</summary>
    public static void Save(bool background = false)
    {
        if (!_loaded || !_dirty)
        {
            return;
        }

        _dirty = false;

        // снимок — здесь, в потоке окна: строки терминала меняются только в нём
        var terminal = new TerminalFile { SavedAt = DateTime.Now };

        foreach (var workspace in AppState.Current.AllWorkspaces)
        {
            if (workspace.TerminalState() is { } tab)
            {
                terminal.Tabs[workspace.Id] = tab;
            }
        }

        var journal = AppLog.Entries
            .Select(e => new JournalEntryState
            {
                Time = e.Time,
                Level = e.Level,
                Text = e.Text,
                Scope = e.Scope,
                Section = e.Section,
                IsServerLine = e.IsServerLine,
            })
            .ToList();

        void Write()
        {
            lock (WriteSync)
            {
                WriteFile(AppPaths.TerminalFile, terminal);
                WriteFile(AppPaths.JournalFile, journal);
            }
        }

        if (background)
        {
            Task.Run(Write);
        }
        else
        {
            Write();
        }
    }

    private static T? Read<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) : null;
        }
        catch (Exception ex)
        {
            AppLog.Debug($"Не удалось прочитать {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    private static void WriteFile<T>(string path, T value)
    {
        try
        {
            AtomicFile.Write(path, JsonSerializer.Serialize(value, JsonOptions));
        }
        catch (Exception ex)
        {
            AppLog.Debug($"Не удалось сохранить {Path.GetFileName(path)}: {ex.Message}");
        }
    }
}
