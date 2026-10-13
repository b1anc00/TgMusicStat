using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using TL;

namespace TGMusicStat.Telegram;

public class TelegramClientService : IAsyncDisposable
{
    private static readonly string SessionDirectory = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "TGMusicStat");

    private static readonly string SessionPath = Path.Combine(
        SessionDirectory,
        "WTelegram.session");

    private static readonly string PhonePath = Path.Combine(
        SessionDirectory,
        "telegram_phone.txt");

    private readonly SemaphoreSlim bioSignal = new(0, 1);
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private readonly object bioLock = new();

    private WTelegram.Client client;

    private Task? bioWorker;

    private string? phoneNumber;
    private string? desiredBio;

    private long desiredVersion;
    private long completedVersion;
    private long failedVersion;

    private bool networkAvailable = true;

    private DateTime lastBioUpdateUtc = DateTime.MinValue;

    public bool IsLoggedIn => client.User != null;

    public string? AccountName
    {
        get
        {
            if (client.User == null)
                return null;

            if (!string.IsNullOrWhiteSpace(client.User.username))
                return $"@{client.User.username}";

            return $"{client.User.first_name} {client.User.last_name}".Trim();
        }
    }

    public TelegramClientService()
    {
        Directory.CreateDirectory(SessionDirectory);

        WTelegram.Helpers.Log = (_, _) => { };

        phoneNumber = LoadPhoneNumber();

        client = CreateClient();
    }

    private WTelegram.Client CreateClient()
    {
        var newClient = new WTelegram.Client(Config)
        {
            TcpHandler = ConnectViaProxyAsync,
            MaxAutoReconnects = 1
        };

        newClient.OnOther += OnOther;

        return newClient;
    }

    private string? Config(string what)
    {
        return what switch
        {
            "api_id" => TelegramSettings.ApiId,
            "api_hash" => TelegramSettings.ApiHash,
            "phone_number" => phoneNumber,
            "session_pathname" => SessionPath,
            _ => null
        };
    }

    public async Task<bool> TryLoginSavedAsync()
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
            return false;

        if (!File.Exists(SessionPath))
            return false;

        try
        {
            var requirement = await AdvanceLoginAsync(phoneNumber);

            if (!IsLoggedIn)
                return false;

            EnsureBioWorker();

            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string?> BeginLoginAsync(string phone)
    {
        phoneNumber = phone.Trim();

        return await AdvanceLoginAsync(phoneNumber);
    }

    public async Task<string?> ContinueLoginAsync(string value)
    {
        return await AdvanceLoginAsync(value);
    }

    private async Task<string?> AdvanceLoginAsync(string? value)
    {
        for (var i = 0; i < 5; i++)
        {
            var requirement = await client.Login(value);

            if (client.User != null)
            {
                SavePhoneNumber();

                EnsureBioWorker();

                return null;
            }

            if (!string.IsNullOrWhiteSpace(requirement))
                return requirement;

            value = null;
        }

        return null;
    }

    private void EnsureBioWorker()
    {
        if (bioWorker is { IsCompleted: false })
            return;

        bioWorker = Task.Run(
            () => BioWorkerAsync(cancellationTokenSource.Token));
    }

    public async Task CancelLoginAsync()
    {
        if (IsLoggedIn)
            return;

        try
        {
            client.OnOther -= OnOther;

            await client.DisposeAsync();
        }
        catch
        {
        }

        if (File.Exists(SessionPath))
            File.Delete(SessionPath);

        phoneNumber = LoadPhoneNumber();

        client = CreateClient();
    }

    public async Task LogoutAsync()
    {
        try
        {
            if (IsLoggedIn)
                await client.Auth_LogOut();
        }
        catch
        {
        }

        try
        {
            client.OnOther -= OnOther;

            await client.DisposeAsync();
        }
        catch
        {
        }

        if (File.Exists(SessionPath))
            File.Delete(SessionPath);

        if (File.Exists(PhonePath))
            File.Delete(PhonePath);

        phoneNumber = null;
    }

    public async Task<string> GetBioAsync()
    {
        if (!IsLoggedIn)
            throw new InvalidOperationException(
                "Telegram не авторизован.");

        var fullUser =
            await client.Users_GetFullUser(new InputUserSelf());

        return fullUser.full_user.about ?? "";
    }

    public void SetNetworkAvailable(bool available)
    {
        networkAvailable = available;

        if (available)
            SignalBioWorker();
    }

    public string? GetDesiredBio()
    {
        lock (bioLock)
            return desiredBio;
    }

    public long QueueBioUpdate(string bio)
    {
        if (!IsLoggedIn)
            return -1;

        long version;

        lock (bioLock)
        {
            desiredBio = bio;
            version = ++desiredVersion;
        }

        SignalBioWorker();

        return version;
    }

    public async Task<bool> SetBioAsync(
        string bio,
        TimeSpan? timeout = null)
    {
        if (!IsLoggedIn)
            return false;

        var version = QueueBioUpdate(bio);

        if (version < 0)
            return false;

        var end =
            DateTime.UtcNow +
            (timeout ?? TimeSpan.FromSeconds(10));

        while (DateTime.UtcNow < end)
        {
            lock (bioLock)
            {
                if (completedVersion >= version)
                    return true;

                if (failedVersion >= version)
                    return false;
            }

            try
            {
                await Task.Delay(
                    50,
                    cancellationTokenSource.Token);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }

    private async Task BioWorkerAsync(
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await bioSignal.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                if (!networkAvailable || !IsLoggedIn)
                    break;

                string bio;
                long version;

                lock (bioLock)
                {
                    var handled =
                        Math.Max(
                            completedVersion,
                            failedVersion);

                    if (desiredBio == null ||
                        desiredVersion <= handled)
                    {
                        break;
                    }

                    bio = desiredBio;
                    version = desiredVersion;
                }

                try
                {
                    await WaitForRateLimitAsync(cancellationToken);

                    lock (bioLock)
                    {
                        bio = desiredBio ?? bio;
                        version = desiredVersion;
                    }

                    await client.Account_UpdateProfile(about: bio);

                    lastBioUpdateUtc = DateTime.UtcNow;

                    lock (bioLock)
                    {
                        completedVersion =
                            Math.Max(
                                completedVersion,
                                version);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (RpcException ex) when (IsFloodWait(ex))
                {
                    var seconds = GetFloodWaitSeconds(ex);

                    try
                    {
                        await Task.Delay(
                            TimeSpan.FromSeconds(seconds + 1),
                            cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
                catch (RpcException)
                {
                    lock (bioLock)
                    {
                        failedVersion =
                            Math.Max(
                                failedVersion,
                                version);
                    }
                }
                catch
                {
                    break;
                }
            }
        }
    }

    private Task OnOther(IObject obj)
    {
        return Task.CompletedTask;
    }

    private async Task WaitForRateLimitAsync(
        CancellationToken cancellationToken)
    {
        var elapsed =
            DateTime.UtcNow - lastBioUpdateUtc;

        var minimum =
            TimeSpan.FromSeconds(1);

        if (elapsed >= minimum)
            return;

        await Task.Delay(
            minimum - elapsed,
            cancellationToken);
    }

    private async Task<TcpClient> ConnectViaProxyAsync(
        string host,
        int port)
    {
        var tcpClient = new TcpClient();

        try
        {
            await tcpClient.ConnectAsync(
                TelegramSettings.ProxyHost,
                TelegramSettings.ProxyPort,
                cancellationTokenSource.Token);

            var stream = tcpClient.GetStream();

            var request =
                $"CONNECT {host}:{port} HTTP/1.1\r\n" +
                $"Host: {host}:{port}\r\n" +
                "Proxy-Connection: Keep-Alive\r\n\r\n";

            var bytes =
                Encoding.ASCII.GetBytes(request);

            await stream.WriteAsync(
                bytes,
                cancellationTokenSource.Token);

            await stream.FlushAsync(
                cancellationTokenSource.Token);

            var response = new List<byte>();
            var buffer = new byte[1];

            while (true)
            {
                var read = await stream.ReadAsync(
                    buffer,
                    cancellationTokenSource.Token);

                if (read == 0)
                    throw new IOException(
                        "Прокси закрыл соединение.");

                response.Add(buffer[0]);

                var count = response.Count;

                if (count >= 4 &&
                    response[count - 4] == '\r' &&
                    response[count - 3] == '\n' &&
                    response[count - 2] == '\r' &&
                    response[count - 1] == '\n')
                {
                    break;
                }

                if (count > 8192)
                    throw new IOException(
                        "Некорректный ответ прокси.");
            }

            var header =
                Encoding.ASCII.GetString(
                    response.ToArray());

            var firstLine =
                header.Split("\r\n")[0];

            if (!firstLine.Contains(" 200 "))
                throw new IOException(
                    $"Прокси не разрешил соединение: {firstLine}");

            return tcpClient;
        }
        catch
        {
            tcpClient.Dispose();
            throw;
        }
    }

    private void SavePhoneNumber()
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
            return;

        File.WriteAllText(
            PhonePath,
            phoneNumber);
    }

    private static string? LoadPhoneNumber()
    {
        if (!File.Exists(PhonePath))
            return null;

        var phone =
            File.ReadAllText(PhonePath).Trim();

        return string.IsNullOrWhiteSpace(phone)
            ? null
            : phone;
    }

    private static bool IsFloodWait(RpcException ex)
    {
        return ex.Message.Contains(
            "FLOOD_WAIT",
            StringComparison.OrdinalIgnoreCase);
    }

    private static int GetFloodWaitSeconds(RpcException ex)
    {
        var match =
            Regex.Match(
                ex.Message,
                @"FLOOD_WAIT_(\d+)",
                RegexOptions.IgnoreCase);

        if (match.Success &&
            int.TryParse(
                match.Groups[1].Value,
                out var seconds))
        {
            return Math.Clamp(
                seconds,
                1,
                300);
        }

        return 10;
    }

    private void SignalBioWorker()
    {
        if (bioSignal.CurrentCount != 0)
            return;

        try
        {
            bioSignal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        cancellationTokenSource.Cancel();

        SignalBioWorker();

        if (bioWorker != null)
        {
            try
            {
                await Task.WhenAny(
                    bioWorker,
                    Task.Delay(1000));
            }
            catch
            {
            }
        }

        client.OnOther -= OnOther;

        try
        {
            await client.DisposeAsync();
        }
        catch
        {
        }

        bioSignal.Dispose();
        cancellationTokenSource.Dispose();
    }
}