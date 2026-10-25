using System.IO;
using MultiServerSync.Models;

namespace MultiServerSync.Services;

/// <summary>
/// Следит за одной строкой списка копирования: файлом или папкой со всем содержимым.
/// Файлы правят в других программах, пока наше окно открыто и в фокусе, — без слежения
/// таблица так и показывала старую дату до щелчка по окну или кнопки обновления.
///
/// Событий от Windows на одно сохранение приходит пачка, поэтому строка перечитывается
/// после полсекунды затишья. Если Windows не успела передать все изменения (переполнение
/// буфера при массовой правке), просим перечитать весь список.
/// </summary>
public sealed class ItemWatcher : IDisposable
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(500);

    private readonly TransferItem _item;
    private readonly Action<TransferItem> _changed;
    private readonly Action _lost;
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _debounce;

    public ItemWatcher(TransferItem item, Action<TransferItem> changed, Action lost)
    {
        _item = item;
        _changed = changed;
        _lost = lost;
        _debounce = new Timer(_ => _changed(_item));

        try
        {
            _watcher = item.IsDirectory
                ? new FileSystemWatcher(item.FullPath) { IncludeSubdirectories = true }
                : new FileSystemWatcher(
                    Path.GetDirectoryName(item.FullPath) ?? item.FullPath,
                    Path.GetFileName(item.FullPath));

            _watcher.NotifyFilter = NotifyFilters.FileName
                | NotifyFilters.DirectoryName
                | NotifyFilters.LastWrite
                | NotifyFilters.Size;

            // буфер побольше: сборка или синхронизация меняют сотни файлов разом
            _watcher.InternalBufferSize = 64 * 1024;

            _watcher.Changed += OnEvent;
            _watcher.Created += OnEvent;
            _watcher.Deleted += OnEvent;
            _watcher.Renamed += OnEvent;
            _watcher.Error += OnError;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception)
        {
            // путь пропал или недоступен (отключённый сетевой диск) — строка просто не следит,
            // её дату обновят возврат в окно и кнопка, как раньше
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    private void OnEvent(object sender, FileSystemEventArgs e) => _debounce.Change(Quiet, Timeout.InfiniteTimeSpan);

    private void OnError(object sender, ErrorEventArgs e) => _lost();

    public void Dispose()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }

        _debounce.Dispose();
    }
}
