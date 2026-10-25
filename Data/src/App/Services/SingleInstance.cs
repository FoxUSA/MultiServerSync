using System.IO;
using System.IO.Pipes;

namespace MultiServerSync.Services;

/// <summary>
/// Одна программа — одно окно. Повторный запуск не открывает второе окно, а просит
/// уже работающее добавить вкладку: как новый рабочий стол в Windows.
/// </summary>
public static class SingleInstance
{
    private const string NewTabMessage = "new-tab";

    private static readonly string Key =
        "MultiServerSync." + Environment.UserName.Replace('\\', '_').Replace(' ', '_');

    private static Mutex? _mutex;

    /// <summary>true — эта копия стала главной; false — программа уже запущена.</summary>
    public static bool TryBecomePrimary()
    {
        _mutex = new Mutex(initiallyOwned: true, @"Local\" + Key, out var created);

        if (!created)
        {
            _mutex.Dispose();
            _mutex = null;
        }

        return created;
    }

    /// <summary>Просит уже работающую копию открыть новую вкладку. Несколько попыток: она могла ещё стартовать.</summary>
    public static bool SignalNewTab(int attempts = 3)
    {
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", Key, PipeDirection.Out);
                client.Connect(2000);

                using var writer = new StreamWriter(client) { AutoFlush = true };
                writer.WriteLine(NewTabMessage);
                return true;
            }
            catch when (attempt < attempts)
            {
                Thread.Sleep(400);
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>Слушает запросы от новых копий программы. Обработчик вызывается из фонового потока.</summary>
    public static void StartListener(Action onNewTab)
    {
        var thread = new Thread(() => Listen(onNewTab))
        {
            IsBackground = true,
            Name = "control-servers-new-tab",
        };

        thread.Start();
    }

    private static void Listen(Action onNewTab)
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    Key, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None);

                server.WaitForConnection();

                using var reader = new StreamReader(server);

                if (string.Equals(reader.ReadLine()?.Trim(), NewTabMessage, StringComparison.Ordinal))
                {
                    onNewTab();
                }
            }
            catch
            {
                // канал мог оборваться на полуслове — просто ждём следующего клиента
                Thread.Sleep(250);
            }
        }
    }
}
