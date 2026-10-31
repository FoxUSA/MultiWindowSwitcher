using System.IO;
using System.IO.Pipes;

namespace MultiWindowSwitcher.Services;

/// <summary>
/// Одна программа — одна копия. Повторный запуск (ярлыком, лаунчером) не открывает вторую,
/// а просит уже работающую показать переключатель.
/// </summary>
public static class SingleInstance
{
    public const string ShowMessage = "show";

    /// <summary>Аккуратный выход по команде (MultiWindowSwitcher.App.exe --quit) — нужен сборке перед заменой Data.</summary>
    public const string QuitMessage = "quit";

    private static readonly string Key =
        "MultiWindowSwitcher." + Environment.UserName.Replace('\\', '_').Replace(' ', '_');

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

    /// <summary>Просит работающую копию показаться. Несколько попыток: она могла ещё стартовать.</summary>
    public static bool SignalShow(int attempts = 3) => Send(ShowMessage, attempts);

    public static bool Send(string message, int attempts = 3)
    {
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", Key, PipeDirection.Out);
                client.Connect(2000);

                using var writer = new StreamWriter(client) { AutoFlush = true };
                writer.WriteLine(message);
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

    /// <summary>Слушает запросы от новых копий. Обработчик вызывается из фонового потока.</summary>
    public static void StartListener(Action<string> onMessage)
    {
        var thread = new Thread(() => Listen(onMessage))
        {
            IsBackground = true,
            Name = "multiwindowswitcher-show",
        };

        thread.Start();
    }

    private static void Listen(Action<string> onMessage)
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    Key, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None);

                server.WaitForConnection();

                using var reader = new StreamReader(server);

                var message = reader.ReadLine()?.Trim();

                if (!string.IsNullOrEmpty(message))
                {
                    onMessage(message);
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
