using System.Diagnostics;
using TGMusicStat.Media;
using TGMusicStat.Network;
using TGMusicStat.Settings;
using TGMusicStat.Telegram;
using TGMusicStat.UI;

namespace TGMusicStat;

public class TrayApplicationContext : ApplicationContext
{
    private readonly CancellationTokenSource cancellationTokenSource = new();

    private readonly SettingsStore settingsStore;
    private readonly AppSettings settings;

    private readonly SettingsForm settingsForm;
    private readonly NotifyIcon trayIcon;

    private readonly ToolStripMenuItem syncMenuItem;
    private readonly ToolStripMenuItem trackMenuItem;

    private readonly HappMonitor happMonitor;
    private readonly TelegramClientService telegram;
    private readonly MediaWatcher mediaWatcher;

    private readonly string appDirectory;
    private readonly string bioBackupPath;
    private readonly string restartBioPath;

    private string originalBio = "";
    private string? currentTrack;

    private bool hasOriginalBio;
    private bool trayHintShown;
    private bool restartRequested;
    private bool exiting;
    private bool suppressSyncMenuEvent;
    private bool happMonitorStarted;

    public TrayApplicationContext(string[] args)
    {
        settingsStore = new SettingsStore();
        settings = settingsStore.Load();

        settings.StartWithWindows =
            AutoStartManager.IsEnabled();

        appDirectory = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "TGMusicStat");

        Directory.CreateDirectory(appDirectory);

        bioBackupPath = Path.Combine(
            appDirectory,
            "original_bio.txt");

        restartBioPath = Path.Combine(
            appDirectory,
            "restart_bio.txt");

        happMonitor = new HappMonitor();
        telegram = new TelegramClientService();
        mediaWatcher = new MediaWatcher();

        settingsForm = new SettingsForm(settings);

        settingsForm.HiddenToTray += ShowTrayHint;

        trackMenuItem = new ToolStripMenuItem(
            "Музыка не обнаружена")
        {
            Enabled = false
        };

        syncMenuItem = new ToolStripMenuItem(
            "Синхронизация")
        {
            CheckOnClick = true,
            Checked = settings.SyncEnabled
        };

        var openMenuItem =
            new ToolStripMenuItem("Открыть");

        var exitMenuItem =
            new ToolStripMenuItem("Выход");

        openMenuItem.Click += (_, _) =>
            ShowSettings();

        exitMenuItem.Click += async (_, _) =>
            await ExitAsync();

        syncMenuItem.CheckedChanged += (_, _) =>
        {
            if (suppressSyncMenuEvent)
                return;

            settings.SyncEnabled =
                syncMenuItem.Checked;

            settingsStore.Save(settings);

            ApplySyncState();
        };

        var contextMenu =
            new ContextMenuStrip();

        contextMenu.Items.Add(trackMenuItem);
        contextMenu.Items.Add(
            new ToolStripSeparator());

        contextMenu.Items.Add(openMenuItem);
        contextMenu.Items.Add(syncMenuItem);

        contextMenu.Items.Add(
            new ToolStripSeparator());

        contextMenu.Items.Add(exitMenuItem);

        trayIcon = new NotifyIcon
        {
            Icon = settingsForm.Icon,
            Text = "TGMusicStat",
            Visible = true,
            ContextMenuStrip = contextMenu
        };

        trayIcon.DoubleClick += (_, _) =>
            ShowSettings();

        settingsForm.SettingsSaved +=
            OnSettingsSaved;

        settingsForm.TelegramLoginRequested += async () =>
        {
            await LoginTelegramAsync();
        };

        settingsForm.TelegramLogoutRequested += async () =>
        {
            await LogoutTelegramAsync();
        };

        settingsForm.FormClosed += async (_, _) =>
        {
            if (!restartRequested)
                await ExitAsync();
        };

        mediaWatcher.TrackChanged +=
            OnTrackChanged;

        mediaWatcher.MusicStopped +=
            OnMusicStopped;

        var reconnectRestart = args.Contains(
            "--reconnect-restart",
            StringComparer.OrdinalIgnoreCase);

        var startupLaunch = args.Contains(
            "--startup",
            StringComparer.OrdinalIgnoreCase);

        _ = StartAsync(reconnectRestart);

        if (!startupLaunch &&
            !reconnectRestart)
        {
            ShowSettings();
        }
    }

    private async Task StartAsync(
        bool reconnectRestart)
    {
        try
        {
            _ = mediaWatcher.StartAsync(
                cancellationTokenSource.Token);

            if (reconnectRestart)
                await Task.Delay(1500);

            if (!await happMonitor.IsAvailableAsync(
                    cancellationTokenSource.Token))
            {
                await happMonitor.WaitUntilAvailableAsync(
                    cancellationTokenSource.Token);
            }

            var loggedIn =
                await telegram.TryLoginSavedAsync();

            if (!loggedIn)
            {
                settingsForm.SetTelegramAccount(null);

                if (reconnectRestart)
                    settingsForm.RestoreWindow();

                return;
            }

            await PrepareTelegramAsync(
                reconnectRestart);

            StartHappMonitor();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "TGMusicStat",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void StartHappMonitor()
    {
        if (happMonitorStarted)
            return;

        happMonitorStarted = true;

        happMonitor.Lost += OnHappLost;
        happMonitor.Restored += OnHappRestored;

        _ = happMonitor.StartAsync(
            cancellationTokenSource.Token);
    }

    private async Task PrepareTelegramAsync(
        bool reconnectRestart)
    {
        settingsForm.SetTelegramAccount(
            telegram.AccountName);

        if (File.Exists(bioBackupPath))
        {
            originalBio =
                await File.ReadAllTextAsync(
                    bioBackupPath);

            hasOriginalBio = true;

            if (!reconnectRestart)
            {
                await telegram.SetBioAsync(
                    originalBio);
            }
        }
        else
        {
            originalBio =
                await telegram.GetBioAsync();

            hasOriginalBio = true;

            await File.WriteAllTextAsync(
                bioBackupPath,
                originalBio);
        }

        if (reconnectRestart &&
            File.Exists(restartBioPath))
        {
            var restartBio =
                await File.ReadAllTextAsync(
                    restartBioPath);

            if (!string.IsNullOrWhiteSpace(
                    restartBio))
            {
                telegram.QueueBioUpdate(
                    restartBio);
            }

            File.Delete(restartBioPath);

            return;
        }

        ApplySyncState();
    }

    private async Task LoginTelegramAsync()
    {
        if (telegram.IsLoggedIn)
            return;

        if (!await happMonitor.IsAvailableAsync(
                cancellationTokenSource.Token))
        {
            MessageBox.Show(
                "HAPP сейчас недоступен. Сначала запусти его.",
                "TGMusicStat",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);

            return;
        }

        using var loginForm =
            new TelegramLoginForm(telegram);

        var result =
            loginForm.ShowDialog(settingsForm);

        if (result != DialogResult.OK)
            return;

        await PrepareTelegramAsync(false);

        StartHappMonitor();
    }

    private async Task LogoutTelegramAsync()
    {
        if (!telegram.IsLoggedIn)
            return;

        var result = MessageBox.Show(
            "Выйти из Telegram в TGMusicStat?",
            "TGMusicStat",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (result != DialogResult.Yes)
            return;

        if (hasOriginalBio)
        {
            await telegram.SetBioAsync(
                originalBio,
                TimeSpan.FromSeconds(5));
        }

        if (File.Exists(bioBackupPath))
            File.Delete(bioBackupPath);

        if (File.Exists(restartBioPath))
            File.Delete(restartBioPath);

        await telegram.LogoutAsync();

        RestartNormally();
    }

    private void OnTrackChanged(
        TrackInfo track)
    {
        currentTrack =
            track.DisplayName;

        settingsForm.SetCurrentTrack(
            currentTrack);

        UpdateTrayTrack(
            currentTrack);

        if (!settings.SyncEnabled ||
            !telegram.IsLoggedIn)
        {
            return;
        }

        telegram.QueueBioUpdate(
            BuildMusicBio(currentTrack));
    }

    private void OnMusicStopped()
    {
        currentTrack = null;

        settingsForm.SetCurrentTrack(null);

        UpdateTrayTrack(null);

        if (!settings.SyncEnabled ||
            !telegram.IsLoggedIn ||
            !hasOriginalBio)
        {
            return;
        }

        telegram.QueueBioUpdate(
            originalBio);
    }

    private void OnSettingsSaved()
    {
        settingsStore.Save(settings);

        AutoStartManager.SetEnabled(
            settings.StartWithWindows);

        suppressSyncMenuEvent = true;

        syncMenuItem.Checked =
            settings.SyncEnabled;

        suppressSyncMenuEvent = false;

        ApplySyncState();
    }

    private void ApplySyncState()
    {
        if (!telegram.IsLoggedIn ||
            !hasOriginalBio)
        {
            return;
        }

        if (!settings.SyncEnabled)
        {
            telegram.QueueBioUpdate(
                originalBio);

            return;
        }

        if (!string.IsNullOrWhiteSpace(
                currentTrack))
        {
            telegram.QueueBioUpdate(
                BuildMusicBio(currentTrack));

            return;
        }

        telegram.QueueBioUpdate(
            originalBio);
    }

    private void OnHappLost()
    {
        if (!telegram.IsLoggedIn)
            return;

        telegram.SetNetworkAvailable(false);
    }

    private void OnHappRestored()
    {
        if (restartRequested ||
            exiting ||
            !telegram.IsLoggedIn)
        {
            return;
        }

        restartRequested = true;

        var desiredBio =
            telegram.GetDesiredBio();

        if (desiredBio != null)
        {
            File.WriteAllText(
                restartBioPath,
                desiredBio);
        }

        RestartApplication();
    }

    public void ShowTrayHint()
    {
        if (trayHintShown)
            return;

        trayHintShown = true;

        trayIcon.ShowBalloonTip(
            2000,
            "TGMusicStat",
            "Программа продолжает работать в трее.",
            ToolTipIcon.Info);
    }

    private string BuildMusicBio(
        string track)
    {
        const int maxLength = 70;

        var prefix =
            settings.BioPrefix;

        var full =
            $"{prefix}{track}";

        if (full.Length <= maxLength)
            return full;

        var separatorIndex =
            track.IndexOf(
                " - ",
                StringComparison.Ordinal);

        if (separatorIndex >= 0)
        {
            var title =
                track[(separatorIndex + 3)..];

            var titleOnly =
                $"{prefix}{title}";

            if (titleOnly.Length <= maxLength)
                return titleOnly;

            return Shorten(
                prefix,
                title,
                maxLength);
        }

        return Shorten(
            prefix,
            track,
            maxLength);
    }

    private static string Shorten(
        string prefix,
        string title,
        int maxLength)
    {
        var available =
            maxLength -
            prefix.Length -
            3;

        if (available <= 0)
        {
            return prefix[
                ..Math.Min(
                    prefix.Length,
                    maxLength)];
        }

        var shortened =
            title[
                ..Math.Min(
                    title.Length,
                    available)]
            .TrimEnd();

        return $"{prefix}{shortened}...";
    }

    private void UpdateTrayTrack(
        string? track)
    {
        if (settingsForm.InvokeRequired)
        {
            settingsForm.Invoke(
                () => UpdateTrayTrack(track));

            return;
        }

        trackMenuItem.Text =
            string.IsNullOrWhiteSpace(track)
                ? "Музыка не обнаружена"
                : track;

        var tooltip =
            string.IsNullOrWhiteSpace(track)
                ? "TGMusicStat"
                : $"TGMusicStat | {track}";

        trayIcon.Text =
            tooltip.Length > 63
                ? tooltip[..63]
                : tooltip;
    }

    private void ShowSettings()
    {
        if (settingsForm.IsDisposed)
            return;

        settingsForm.RestoreWindow();
    }

    private async Task ExitAsync()
    {
        if (exiting)
            return;

        exiting = true;

        cancellationTokenSource.Cancel();

        if (!restartRequested &&
            telegram.IsLoggedIn &&
            hasOriginalBio)
        {
            var restored =
                await telegram.SetBioAsync(
                    originalBio,
                    TimeSpan.FromSeconds(5));

            if (restored &&
                File.Exists(bioBackupPath))
            {
                File.Delete(bioBackupPath);
            }
        }

        trayIcon.Visible = false;
        trayIcon.Dispose();

        await telegram.DisposeAsync();

        ExitThread();
    }

    private void RestartNormally()
    {
        var executable =
            Environment.ProcessPath;

        if (string.IsNullOrWhiteSpace(
                executable))
        {
            return;
        }

        trayIcon.Visible = false;

        Process.Start(
            new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = true
            });

        Environment.Exit(0);
    }

    private void RestartApplication()
    {
        var executable =
            Environment.ProcessPath;

        if (string.IsNullOrWhiteSpace(
                executable))
        {
            return;
        }

        trayIcon.Visible = false;

        Process.Start(
            new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "--reconnect-restart",
                UseShellExecute = true
            });

        Environment.Exit(0);
    }
}