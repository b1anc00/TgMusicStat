using System.Net.Sockets;
using TGMusicStat.Telegram;

namespace TGMusicStat.Network;

public class HappMonitor
{
    public event Action? Lost;
    public event Action? Restored;

    public async Task<bool> IsAvailableAsync(
        CancellationToken cancellationToken = default)
    {
        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        timeout.CancelAfter(TimeSpan.FromSeconds(1));

        using var client = new TcpClient();

        try
        {
            await client.ConnectAsync(
                TelegramSettings.ProxyHost,
                TelegramSettings.ProxyPort,
                timeout.Token);

            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task WaitUntilAvailableAsync(
        CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (await IsAvailableAsync(cancellationToken))
                return;

            await Task.Delay(
                1000,
                cancellationToken);
        }
    }

    public async Task StartAsync(
        CancellationToken cancellationToken = default)
    {
        var previousState =
            await IsAvailableAsync(cancellationToken);

        var wasLost = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(
                    1000,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var currentState =
                await IsAvailableAsync(cancellationToken);

            if (previousState && !currentState)
            {
                wasLost = true;
                Lost?.Invoke();
            }
            else if (!previousState && currentState && wasLost)
            {
                Restored?.Invoke();
                return;
            }

            previousState = currentState;
        }
    }
}