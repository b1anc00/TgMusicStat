using Windows.Media.Control;

namespace TGMusicStat.Media;

public class MediaWatcher
{
    private GlobalSystemMediaTransportControlsSessionManager? manager;

    private readonly List<GlobalSystemMediaTransportControlsSession> sessions = [];
    private readonly object sessionsLock = new();
    private readonly object stopTimerLock = new();

    private readonly SemaphoreSlim signal = new(0, 1);

    private CancellationTokenSource? stopDelayCts;

    private string? lastTrack;
    private bool musicActive;

    public event Action<TrackInfo>? TrackChanged;
    public event Action? MusicStopped;

    public async Task StartAsync(
        CancellationToken cancellationToken = default)
    {
        manager =
            await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

        manager.SessionsChanged +=
            OnSessionsChanged;

        RefreshSessions();
        SignalCheck();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await signal.WaitAsync(
                    cancellationToken);

                await CheckCurrentTrackAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            CancelStopTimer();
            UnsubscribeSessions();

            if (manager != null)
            {
                manager.SessionsChanged -=
                    OnSessionsChanged;
            }
        }
    }

    private void OnSessionsChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        SessionsChangedEventArgs args)
    {
        RefreshSessions();
        SignalCheck();
    }

    private void OnMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args)
    {
        SignalCheck();
    }

    private void OnPlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args)
    {
        SignalCheck();
    }

    private void RefreshSessions()
    {
        if (manager == null)
            return;

        lock (sessionsLock)
        {
            foreach (var session in sessions)
            {
                session.MediaPropertiesChanged -=
                    OnMediaPropertiesChanged;

                session.PlaybackInfoChanged -=
                    OnPlaybackInfoChanged;
            }

            sessions.Clear();

            sessions.AddRange(
                manager.GetSessions());

            foreach (var session in sessions)
            {
                session.MediaPropertiesChanged +=
                    OnMediaPropertiesChanged;

                session.PlaybackInfoChanged +=
                    OnPlaybackInfoChanged;
            }
        }
    }

    private void UnsubscribeSessions()
    {
        lock (sessionsLock)
        {
            foreach (var session in sessions)
            {
                session.MediaPropertiesChanged -=
                    OnMediaPropertiesChanged;

                session.PlaybackInfoChanged -=
                    OnPlaybackInfoChanged;
            }

            sessions.Clear();
        }
    }

    private void SignalCheck()
    {
        try
        {
            signal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private async Task CheckCurrentTrackAsync()
    {
        GlobalSystemMediaTransportControlsSession[] currentSessions;

        lock (sessionsLock)
        {
            currentSessions =
                sessions.ToArray();
        }

        TrackInfo? currentTrack = null;

        foreach (var session in currentSessions)
        {
            try
            {
                var playbackInfo =
                    session.GetPlaybackInfo();

                if (playbackInfo.PlaybackStatus !=
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                {
                    continue;
                }

                var properties =
                    await session.TryGetMediaPropertiesAsync();

                var track =
                    ParseTrack(
                        properties.Title,
                        properties.Artist);

                if (track == null)
                    continue;

                currentTrack = track;
                break;
            }
            catch
            {
            }
        }

        if (currentTrack != null)
        {
            CancelStopTimer();

            musicActive = true;

            if (currentTrack.DisplayName ==
                lastTrack)
            {
                return;
            }

            lastTrack =
                currentTrack.DisplayName;

            TrackChanged?.Invoke(
                currentTrack);

            return;
        }

        if (musicActive)
            StartStopTimer();
    }

    private static TrackInfo? ParseTrack(
        string? rawTitle,
        string? rawArtist)
    {
        var title =
            Normalize(rawTitle);

        var artist =
            Normalize(rawArtist);

        if (string.IsNullOrWhiteSpace(title))
            return null;

        title =
            RemoveServiceSuffix(title);

        if (LooksLikeRegularYouTube(title))
            return null;

        if (!string.IsNullOrWhiteSpace(artist))
        {
            artist =
                CleanArtist(artist);

            if (!string.IsNullOrWhiteSpace(artist))
            {
                return new TrackInfo(
                    title,
                    artist);
            }
        }

        if (TrySplitTrack(
                title,
                out var parsedTitle,
                out var parsedArtist))
        {
            return new TrackInfo(
                parsedTitle,
                parsedArtist);
        }

        return null;
    }

    private static bool TrySplitTrack(
        string value,
        out string title,
        out string artist)
    {
        if (TrySplit(
                value,
                " • ",
                out title,
                out artist))
        {
            return true;
        }

        if (TrySplit(
                value,
                " · ",
                out title,
                out artist))
        {
            return true;
        }

        if (TrySplitBy(
                value,
                out title,
                out artist))
        {
            return true;
        }

        if (TrySplit(
                value,
                " — ",
                out title,
                out artist))
        {
            return true;
        }

        if (TrySplit(
                value,
                " – ",
                out title,
                out artist))
        {
            return true;
        }

        if (TrySplit(
                value,
                " - ",
                out title,
                out artist))
        {
            return true;
        }

        title = "";
        artist = "";

        return false;
    }

    private static bool TrySplit(
        string value,
        string separator,
        out string title,
        out string artist)
    {
        var index =
            value.LastIndexOf(
                separator,
                StringComparison.Ordinal);

        if (index <= 0)
        {
            title = "";
            artist = "";

            return false;
        }

        var left =
            value[..index].Trim();

        var right =
            value[(index + separator.Length)..].Trim();

        if (string.IsNullOrWhiteSpace(left) ||
            string.IsNullOrWhiteSpace(right))
        {
            title = "";
            artist = "";

            return false;
        }

        title = left;
        artist = CleanArtist(right);

        return !string.IsNullOrWhiteSpace(artist);
    }

    private static bool TrySplitBy(
        string value,
        out string title,
        out string artist)
    {
        var index =
            value.LastIndexOf(
                " by ",
                StringComparison.OrdinalIgnoreCase);

        if (index <= 0)
        {
            title = "";
            artist = "";

            return false;
        }

        title =
            value[..index].Trim();

        artist =
            CleanArtist(
                value[(index + 4)..]);

        return
            !string.IsNullOrWhiteSpace(title) &&
            !string.IsNullOrWhiteSpace(artist);
    }

    private static string CleanArtist(
        string value)
    {
        var artist =
            Normalize(value);

        var bulletIndex =
            artist.IndexOf('•');

        if (bulletIndex > 0)
        {
            artist =
                artist[..bulletIndex].Trim();
        }

        var middleDotIndex =
            artist.IndexOf('·');

        if (middleDotIndex > 0)
        {
            artist =
                artist[..middleDotIndex].Trim();
        }

        return artist;
    }

    private static string RemoveServiceSuffix(
        string value)
    {
        string[] suffixes =
        [
            " - YouTube Music",
            " — YouTube Music",
            " – YouTube Music",
            " | YouTube Music",

            " - SoundCloud",
            " — SoundCloud",
            " – SoundCloud",
            " | SoundCloud",

            " - Яндекс Музыка",
            " — Яндекс Музыка",
            " – Яндекс Музыка",
            " | Яндекс Музыка",

            " - Yandex Music",
            " — Yandex Music",
            " – Yandex Music",
            " | Yandex Music",

            " - VK Музыка",
            " — VK Музыка",
            " | VK Музыка",

            " - Apple Music",
            " | Apple Music",

            " - Deezer",
            " | Deezer",

            " - TIDAL",
            " | TIDAL",

            " - Bandcamp",
            " | Bandcamp"
        ];

        foreach (var suffix in suffixes)
        {
            if (!value.EndsWith(
                    suffix,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return value[
                ..^suffix.Length]
                .Trim();
        }

        return value;
    }

    private static bool LooksLikeRegularYouTube(
        string title)
    {
        return title.EndsWith(
            " - YouTube",
            StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        return value
            .Replace('\u00A0', ' ')
            .Trim();
    }

    private void StartStopTimer()
    {
        lock (stopTimerLock)
        {
            if (stopDelayCts != null)
                return;

            stopDelayCts =
                new CancellationTokenSource();

            _ = WaitForStopAsync(
                stopDelayCts);
        }
    }

    private async Task WaitForStopAsync(
        CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(
                TimeSpan.FromSeconds(30),
                cts.Token);

            musicActive = false;
            lastTrack = null;

            MusicStopped?.Invoke();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (stopTimerLock)
            {
                if (ReferenceEquals(
                        stopDelayCts,
                        cts))
                {
                    stopDelayCts = null;
                }
            }

            cts.Dispose();
        }
    }

    private void CancelStopTimer()
    {
        CancellationTokenSource? cts;

        lock (stopTimerLock)
        {
            cts = stopDelayCts;
            stopDelayCts = null;
        }

        cts?.Cancel();
    }
}