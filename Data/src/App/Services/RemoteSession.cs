using System.Net.Sockets;
using MultiServerSync.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace MultiServerSync.Services;

/// <summary>
/// Одно SFTP-подключение на всё окно обзора — как сессия в WinSCP.
/// Раньше каждый переход и каждое раскрытие ветки заново проходили рукопожатие и вход
/// по паролю, и окно ждало секунду-две на любой щелчок. Теперь вход один, а прочитанные
/// папки лежат в кэше: возврат назад и дерево слева показываются сразу.
/// </summary>
public sealed class RemoteSession : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, IReadOnlyList<RemoteEntry>> _cache = new(StringComparer.Ordinal);
    private readonly AppSettings _settings;
    private SftpClient? _sftp;
    private bool _disposed;

    public RemoteSession(ServerEntry server, AppSettings settings)
    {
        Server = server;
        _settings = settings;
    }

    public ServerEntry Server { get; }

    /// <summary>Домашняя папка пользователя — куда сервер пускает сразу после входа.</summary>
    public string? Home { get; private set; }

    /// <summary>Папка уже прочитана — показать можно без похода на сервер.</summary>
    public bool IsCached(string path) => _cache.ContainsKey(SshService.NormalizeRemote(path));

    /// <param name="refresh">Перечитать с сервера, даже если папка есть в кэше.</param>
    public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, bool refresh = false)
    {
        var target = SshService.NormalizeRemote(path);

        if (!refresh && _cache.TryGetValue(target, out var hit))
        {
            return hit;
        }

        await _gate.WaitAsync();

        try
        {
            // пока ждали очереди, ту же папку мог прочитать соседний запрос
            if (!refresh && _cache.TryGetValue(target, out hit))
            {
                return hit;
            }

            var list = await Task.Run(() => Run(sftp => SshService.ReadDirectory(sftp, target)));
            _cache[target] = list;
            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CreateDirectoryAsync(string path)
    {
        var target = SshService.NormalizeRemote(path);

        await _gate.WaitAsync();

        try
        {
            await Task.Run(() => Run(sftp =>
            {
                SshService.CreateDirectory(sftp, target);
                return true;
            }));

            _cache.Remove(SshService.ParentRemote(target));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Подключается заранее, чтобы первый щелчок не ждал входа.</summary>
    public async Task ConnectAsync()
    {
        await _gate.WaitAsync();

        try
        {
            await Task.Run(() => Run(_ => true));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Выполняет работу на подключении. Оборвалось (сервер закрыл простаивающую сессию,
    /// пропала сеть) — переподключаемся один раз и пробуем снова.
    /// </summary>
    private T Run<T>(Func<SftpClient, T> work)
    {
        for (var attempt = 0; ; attempt++)
        {
            var sftp = Connected();

            try
            {
                return work(sftp);
            }
            catch (Exception ex) when (attempt == 0 && IsBroken(ex))
            {
                Drop();
            }
        }
    }

    private static bool IsBroken(Exception ex) =>
        ex is SshConnectionException or SocketException or ObjectDisposedException
        || ex is SshOperationTimeoutException;

    private SftpClient Connected()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_sftp is { IsConnected: true })
        {
            return _sftp;
        }

        Drop();

        var sftp = new SftpClient(SshService.BuildConnectionInfo(Server, _settings))
        {
            // без этого сервер рвёт простаивающую сессию, пока человек думает над папкой
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        };

        sftp.Connect();
        Home ??= SshService.NormalizeRemote(sftp.WorkingDirectory);

        _sftp = sftp;
        return sftp;
    }

    private void Drop()
    {
        var sftp = _sftp;
        _sftp = null;

        if (sftp is null)
        {
            return;
        }

        try
        {
            sftp.Dispose();
        }
        catch
        {
            // уже оборвано — закрывать нечего
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cache.Clear();

        // закрытие сессии может подождать ответа сервера — окно на это не задерживаем
        var sftp = _sftp;
        _sftp = null;

        if (sftp is not null)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    sftp.Dispose();
                }
                catch
                {
                    // сеть уже пропала — не важно
                }
            });
        }
    }
}
