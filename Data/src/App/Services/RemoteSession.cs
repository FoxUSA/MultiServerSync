using System.Collections.Concurrent;
using System.Net.Sockets;
using MultiServerSync.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace MultiServerSync.Services;

/// <summary>
/// Подключение к серверу для окна обзора — как сессия в WinSCP.
/// Раньше каждый переход и каждое раскрытие ветки заново проходили рукопожатие и вход
/// по паролю, и окно ждало секунду-две на любой щелчок. Теперь вход один, а прочитанные
/// папки лежат в кэше: возврат назад и дерево слева показываются сразу.
/// Папка читается одной командой find по SSH (содержимое плюс то, у каких подпапок есть свои
/// папки); где find урезанный — по SFTP, как раньше.
/// Сессия переживает закрытие окна: следующее открытие обзора того же сервера в течение
/// <see cref="IdleLife"/> не подключается заново и показывает прочитанное мгновенно.
/// </summary>
public sealed class RemoteSession : IDisposable
{
    /// <summary>Сколько сессия без окон держит подключение и кэш.</summary>
    private static readonly TimeSpan IdleLife = TimeSpan.FromMinutes(10);

    /// <summary>Сколько прочитанная папка считается свежей: старше — показывается и тихо перечитывается.</summary>
    private static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(20);

    private static readonly Lock PoolGate = new();
    private static readonly Dictionary<string, RemoteSession> Pool = new(StringComparer.Ordinal);
    private static readonly Timer Sweeper = new(_ => Sweep(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, (IReadOnlyList<RemoteEntry> Items, DateTime At)> _cache = new(StringComparer.Ordinal);

    /// <summary>Есть ли у папки свои подпапки — для стрелки в дереве. Узнаётся при чтении родителя.</summary>
    private readonly ConcurrentDictionary<string, bool> _hasSubdirs = new(StringComparer.Ordinal);

    private readonly AppSettings _settings;
    private readonly string _signature;
    private SshClient? _ssh;
    private SftpClient? _sftp;

    /// <summary>find на сервере без -printf — папки читаются по SFTP.</summary>
    private bool _noFind;

    private bool _disposed;
    private int _users;
    private DateTime _idleSince = DateTime.Now;

    private RemoteSession(ServerEntry server, AppSettings settings, string signature)
    {
        Server = server;
        _settings = settings;
        _signature = signature;
    }

    /// <summary>
    /// Сессия сервера: живая из запаса или новая. Сервер правили (адрес, вход, промежуточный) —
    /// старая выбрасывается. Вернуть — <see cref="Release"/>, когда окно закрылось.
    /// </summary>
    public static RemoteSession Acquire(ServerEntry server, AppSettings settings)
    {
        var signature = Signature(server);
        RemoteSession? stale = null;
        RemoteSession session;

        lock (PoolGate)
        {
            if (Pool.TryGetValue(server.Id, out var found) && !found._disposed && found._signature == signature)
            {
                session = found;
            }
            else
            {
                stale = found;
                session = new RemoteSession(server, settings, signature);
                Pool[server.Id] = session;
            }

            session._users++;
        }

        stale?.Dispose();
        return session;
    }

    /// <summary>Окно закрылось или ушло на другой сервер: сессия остаётся в запасе.</summary>
    public void Release()
    {
        lock (PoolGate)
        {
            _users = Math.Max(0, _users - 1);
            _idleSince = DateTime.Now;
        }
    }

    /// <summary>Всё, от чего зависит подключение: поменялось — старое подключение не годится.</summary>
    private static string Signature(ServerEntry s) =>
        string.Join('\u001F', s.Host, s.Port, s.User, s.Password, s.KeyPath, s.KeyPassphrase, s.JumpServerId);

    private static void Sweep()
    {
        List<RemoteSession> idle;

        lock (PoolGate)
        {
            idle = Pool.Values.Where(s => s._users == 0 && DateTime.Now - s._idleSince > IdleLife).ToList();

            foreach (var session in idle)
            {
                Pool.Remove(session.Server.Id);
            }
        }

        foreach (var session in idle)
        {
            session.Dispose();
        }
    }

    public ServerEntry Server { get; }

    /// <summary>Домашняя папка пользователя — куда сервер пускает сразу после входа.</summary>
    public string? Home { get; private set; }

    /// <summary>Папка уже прочитана — показать можно без похода на сервер.</summary>
    public bool IsCached(string path) => _cache.ContainsKey(SshService.NormalizeRemote(path));

    /// <summary>Прочитана недавно — перечитывать незачем.</summary>
    public bool IsFresh(string path) =>
        _cache.TryGetValue(SshService.NormalizeRemote(path), out var hit) && DateTime.Now - hit.At < FreshFor;

    /// <summary>Прочитанная папка из кэша, если она там есть.</summary>
    public IReadOnlyList<RemoteEntry>? Cached(string path) =>
        _cache.TryGetValue(SshService.NormalizeRemote(path), out var hit) ? hit.Items : null;

    /// <summary>Есть ли в папке свои папки; null — пока неизвестно.</summary>
    public bool? HasSubdirs(string path) =>
        _hasSubdirs.TryGetValue(SshService.NormalizeRemote(path), out var has) ? has : null;

    /// <summary>
    /// Запасной путь для серверов без полноценного find: папки читаются заранее, в фоне
    /// и параллельно по SFTP — так дерево узнаёт, у каких папок нет подпапок. Ошибки молча —
    /// непрочитанная папка просто остаётся со стрелкой.
    /// </summary>
    public async Task PrefetchAsync(IReadOnlyList<string> paths)
    {
        var todo = paths
            .Select(SshService.NormalizeRemote)
            .Where(p => !_cache.ContainsKey(p) && !_hasSubdirs.ContainsKey(p))
            .Distinct()
            .ToList();

        if (todo.Count == 0)
        {
            return;
        }

        try
        {
            await Task.Run(() => RunSftp(_ => true));
        }
        catch
        {
            return;
        }

        await Task.Run(() => Parallel.ForEach(
            todo,
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            path =>
            {
                if (_disposed || _sftp is not { IsConnected: true } sftp)
                {
                    return;
                }

                try
                {
                    var items = SshService.ReadDirectory(sftp, path);
                    _cache.TryAdd(path, (items, DateTime.Now));
                    _hasSubdirs[path] = items.Any(i => i.IsDirectory);
                }
                catch (Exception ex) when (ex is SshException or SocketException or ObjectDisposedException or InvalidOperationException)
                {
                    // нет прав или папка пропала — останется со стрелкой
                }
            }));
    }

    /// <param name="refresh">Перечитать с сервера, даже если папка есть в кэше.</param>
    public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, bool refresh = false)
    {
        var target = SshService.NormalizeRemote(path);

        if (!refresh && _cache.TryGetValue(target, out var hit))
        {
            return hit.Items;
        }

        await _gate.WaitAsync();

        try
        {
            // пока ждали очереди, ту же папку мог прочитать соседний запрос
            if (!refresh && _cache.TryGetValue(target, out hit))
            {
                return hit.Items;
            }

            var list = await Task.Run(() => Read(target));
            _cache[target] = (list, DateTime.Now);
            _hasSubdirs[target] = list.Any(i => i.IsDirectory);
            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Папка одной командой find; не вышло (урезанный find) — по SFTP, и дальше всегда так.</summary>
    private IReadOnlyList<RemoteEntry> Read(string target)
    {
        (IReadOnlyList<RemoteEntry> Items, HashSet<string> WithSubdirs)? fast = null;

        if (!_noFind)
        {
            try
            {
                fast = Run(ssh => SshService.ReadDirectoryFast(ssh, target));
            }
            catch (SshException ex) when (ex is not (SftpPathNotFoundException or SftpPermissionDeniedException))
            {
                // команды на сервере запрещены (вход только для SFTP) — читаем по SFTP
            }
        }

        if (fast is { } found)
        {
            var fastItems = found;

            foreach (var dir in fastItems.Items.Where(i => i.IsDirectory))
            {
                _hasSubdirs[dir.FullPath] = fastItems.WithSubdirs.Contains(dir.Name);
            }

            return fastItems.Items;
        }

        _noFind = true;
        return RunSftp(sftp => SshService.ReadDirectory(sftp, target));
    }

    /// <summary>Своя операция поменяла папку — следующее чтение пойдёт на сервер, а не в кэш.</summary>
    public void Invalidate(string path)
    {
        var target = SshService.NormalizeRemote(path);
        _cache.TryRemove(target, out _);
        _hasSubdirs.TryRemove(target, out _);
    }

    /// <summary>
    /// Чтение на уровень вперёд: подпапки открытой папки — одной командой, в кэш. Идёт мимо
    /// очереди и только по живому подключению: переход по щелчку его не ждёт. Ошибки молча —
    /// непрочитанная папка просто прочитается, когда в неё зайдут.
    /// </summary>
    public async Task PrefetchFastAsync(IReadOnlyList<string> paths)
    {
        if (_noFind)
        {
            await PrefetchAsync(paths);
            return;
        }

        if (_disposed || _ssh is not { IsConnected: true } ssh)
        {
            return;
        }

        var todo = paths
            .Select(SshService.NormalizeRemote)
            .Where(p => p != "/" && !IsFresh(p))
            .Distinct()
            .Take(PrefetchDirs)
            .ToList();

        if (todo.Count == 0)
        {
            return;
        }

        try
        {
            var read = await Task.Run(() => SshService.ReadDirectoriesFast(ssh, todo, PrefetchEntries, CancellationToken.None));

            foreach (var (dir, items) in read)
            {
                _cache[dir] = (items, DateTime.Now);
                _hasSubdirs[dir] = items.Any(i => i.IsDirectory);
            }
        }
        catch (Exception ex) when (ex is SshException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            // подключение оборвалось — следующий переход переподключится сам
        }
    }

    /// <summary>Сколько подпапок читается наперёд за раз.</summary>
    private const int PrefetchDirs = 300;

    /// <summary>Сколько записей наперёд — больше уже не «мгновенно», а лишний трафик.</summary>
    private const int PrefetchEntries = 50_000;

    /// <summary>
    /// Служебный скрипт на сервере: удалить, скопировать, права. Очередь сессии держится только
    /// на время подключения — долгая команда не мешает ходить по папкам. Оборвалось — один повтор.
    /// </summary>
    public async Task<(string Output, string Error)> ExecAsync(string script, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            var ssh = await ReadyAsync(() => ConnectedSsh());

            try
            {
                return await Task.Run(() => SshService.RunInput(ssh, script, ct), ct);
            }
            catch (Exception ex) when (attempt == 0 && IsBroken(ex))
            {
                await DropBrokenAsync(ssh);
            }
        }
    }

    /// <summary>То же по SFTP: переименовать, создать файл, атрибуты.</summary>
    public async Task<T> SftpAsync<T>(Func<SftpClient, T> work)
    {
        for (var attempt = 0; ; attempt++)
        {
            var sftp = await ReadyAsync(() => ConnectedSftp());

            try
            {
                return await Task.Run(() => work(sftp));
            }
            catch (Exception ex) when (attempt == 0 && IsBroken(ex))
            {
                await DropBrokenAsync(sftp);
            }
        }
    }

    private async Task<T> ReadyAsync<T>(Func<T> connect)
    {
        await _gate.WaitAsync();

        try
        {
            return await Task.Run(connect);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DropBrokenAsync(BaseClient broken)
    {
        await _gate.WaitAsync();

        try
        {
            if (ReferenceEquals(_ssh, broken))
            {
                Drop(ref _ssh);
            }
            else if (ReferenceEquals(_sftp, broken))
            {
                Drop(ref _sftp);
            }
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
            await Task.Run(() => RunSftp(sftp =>
            {
                SshService.CreateDirectory(sftp, target);
                return true;
            }));

            var parent = SshService.ParentRemote(target);
            _cache.TryRemove(parent, out _);
            _hasSubdirs[parent] = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Подключается заранее, чтобы первый щелчок не ждал входа. Живое подключение — сразу.</summary>
    public async Task ConnectAsync()
    {
        if (_ssh is { IsConnected: true })
        {
            return;
        }

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
    private T Run<T>(Func<SshClient, T> work)
    {
        for (var attempt = 0; ; attempt++)
        {
            var ssh = ConnectedSsh();

            try
            {
                return work(ssh);
            }
            catch (Exception ex) when (attempt == 0 && IsBroken(ex))
            {
                Drop(ref _ssh);
            }
        }
    }

    /// <summary>То же на SFTP: создание папок и серверы без полноценного find.</summary>
    private T RunSftp<T>(Func<SftpClient, T> work)
    {
        for (var attempt = 0; ; attempt++)
        {
            var sftp = ConnectedSftp();

            try
            {
                return work(sftp);
            }
            catch (Exception ex) when (attempt == 0 && IsBroken(ex))
            {
                Drop(ref _sftp);
            }
        }
    }

    private static bool IsBroken(Exception ex) =>
        ex is SshConnectionException or SocketException or ObjectDisposedException
        || ex is SshOperationTimeoutException;

    private SshClient ConnectedSsh()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_ssh is { IsConnected: true })
        {
            return _ssh;
        }

        Drop(ref _ssh);

        var ssh = SshService.NewSsh(Server, _settings);

        // без этого сервер рвёт простаивающую сессию, пока человек думает над папкой
        ssh.KeepAliveInterval = TimeSpan.FromSeconds(30);

        try
        {
            ssh.Connect();
        }
        catch
        {
            ssh.Dispose();
            throw;
        }

        if (Home is null)
        {
            try
            {
                using var pwd = ssh.RunCommand("pwd");
                var home = pwd.Result.Trim();
                Home = home.StartsWith('/') ? SshService.NormalizeRemote(home) : null;
            }
            catch (SshException)
            {
                // команды запрещены — домашнюю папку скажет SFTP
            }
        }

        _ssh = ssh;
        return ssh;
    }

    private SftpClient ConnectedSftp()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_sftp is { IsConnected: true })
        {
            return _sftp;
        }

        Drop(ref _sftp);

        var sftp = SshService.NewSftp(Server, _settings);
        sftp.KeepAliveInterval = TimeSpan.FromSeconds(30);

        try
        {
            sftp.Connect();
        }
        catch
        {
            sftp.Dispose();
            throw;
        }

        Home ??= SshService.NormalizeRemote(sftp.WorkingDirectory);

        _sftp = sftp;
        return sftp;
    }

    private static void Drop<T>(ref T? client)
        where T : BaseClient
    {
        var old = client;
        client = null;

        if (old is null)
        {
            return;
        }

        try
        {
            old.Dispose();
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
        _hasSubdirs.Clear();

        // закрытие сессии может подождать ответа сервера — окно на это не задерживаем
        BaseClient?[] clients = [_ssh, _sftp];
        _ssh = null;
        _sftp = null;

        _ = Task.Run(() =>
        {
            foreach (var client in clients)
            {
                try
                {
                    client?.Dispose();
                }
                catch
                {
                    // сеть уже пропала — не важно
                }
            }
        });
    }
}
