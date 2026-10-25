using MultiServerSync.Models;
using Renci.SshNet;

namespace MultiServerSync.Services;

/// <summary>
/// Туннель через промежуточный сервер: подключение к нему и проброс локального порта
/// к целевому серверу. Живёт, пока жив клиент, которому он нужен.
/// </summary>
public sealed class JumpTunnel(SshClient client, ForwardedPortLocal forward) : IDisposable
{
    public int Port => (int)forward.BoundPort;

    public void Dispose()
    {
        try
        {
            forward.Stop();
        }
        catch
        {
            // уже остановлен
        }

        forward.Dispose();
        client.Dispose();
    }
}

/// <summary>
/// Промежуточный сервер (jump host, как ProxyJump у ssh). У промежуточного может быть свой
/// промежуточный — цепочка разворачивается сама; замкнутую на себя цепочку не пускаем.
/// </summary>
internal static class Jump
{
    /// <summary>Глубже — это уже не цепочка, а петля в настройках.</summary>
    private const int MaxDepth = 4;

    [ThreadStatic]
    private static int _depth;

    /// <summary>Туннель к серверу, если он за промежуточным; иначе null — подключаться напрямую.</summary>
    public static JumpTunnel? Open(ServerEntry server, AppSettings settings)
    {
        if (server.JumpServerId.Length == 0)
        {
            return null;
        }

        var jump = AppState.Current.Servers.FirstOrDefault(s => s.Id == server.JumpServerId)
            ?? throw new InvalidOperationException(
                $"Промежуточный сервер для «{server.PaneLabel}» удалён — выберите другой или «напрямую» в окне сервера.");

        if (_depth >= MaxDepth || jump.Id == server.Id)
        {
            throw new InvalidOperationException(
                $"Цепочка промежуточных серверов у «{server.PaneLabel}» замкнута сама на себя — проверьте поле «Через сервер».");
        }

        _depth++;
        SshClient? client = null;

        try
        {
            client = SshService.NewSsh(jump, settings);
            client.Connect();

            var forward = new ForwardedPortLocal("127.0.0.1", 0, server.Host.Trim(), (uint)(server.Port <= 0 ? 22 : server.Port));
            client.AddForwardedPort(forward);
            forward.Start();

            return new JumpTunnel(client, forward);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            client?.Dispose();

            // ошибка про промежуточный сервер, а не про целевой — иначе «не пустили root» вводит в заблуждение
            throw new InvalidOperationException($"Промежуточный сервер «{jump.PaneLabel}»: {SshService.Describe(ex, jump)}", ex);
        }
        finally
        {
            _depth--;
        }
    }
}

/// <summary>Клиент SSH через туннель: закрывается — закрывает и туннель.</summary>
internal sealed class TunneledSshClient(ConnectionInfo info, JumpTunnel tunnel) : SshClient(info)
{
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            tunnel.Dispose();
        }
    }
}

/// <summary>Клиент SFTP через туннель.</summary>
internal sealed class TunneledSftpClient(ConnectionInfo info, JumpTunnel tunnel) : SftpClient(info)
{
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            tunnel.Dispose();
        }
    }
}
