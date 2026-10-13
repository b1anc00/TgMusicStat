using TGMusicStat.Settings;

namespace TGMusicStat.UI;

public class SettingsForm : Form
{
    private readonly Label telegramStatusLabel;
    private readonly Button telegramButton;

    private readonly Label trackLabel;

    private readonly Label prefixValueLabel;
    private readonly Button editPrefixButton;

    private readonly TextBox prefixTextBox;
    private readonly Button applyPrefixButton;
    private readonly Button cancelPrefixButton;

    private readonly CheckBox syncCheckBox;
    private readonly CheckBox startWithWindowsCheckBox;

    private readonly Button saveButton;

    private string prefixBeforeEdit = "";
    private bool telegramConnected;

    public AppSettings Settings { get; }

    public event Action? SettingsSaved;
    public event Action? HiddenToTray;

    public event Action? TelegramLoginRequested;
    public event Action? TelegramLogoutRequested;

    public SettingsForm(AppSettings settings)
    {
        Settings = settings;

        Text = "TGMusicStat";
        Width = 520;
        Height = 420;

        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;

        MaximizeBox = false;
        MinimizeBox = true;

        var executablePath = Environment.ProcessPath;

        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            var icon = Icon.ExtractAssociatedIcon(executablePath);

            if (icon != null)
                Icon = icon;
        }

        var telegramTitle = new Label
        {
            Text = "Telegram",
            Left = 20,
            Top = 20,
            Width = 100,
            Height = 20
        };

        telegramStatusLabel = new Label
        {
            Text = "Не подключён",
            Left = 20,
            Top = 45,
            Width = 300,
            Height = 25
        };

        telegramButton = new Button
        {
            Text = "Войти в Telegram",
            Left = 345,
            Top = 35,
            Width = 140,
            Height = 32
        };

        telegramButton.Click += (_, _) =>
        {
            if (telegramConnected)
                TelegramLogoutRequested?.Invoke();
            else
                TelegramLoginRequested?.Invoke();
        };

        var trackTitle = new Label
        {
            Text = "Текущий трек",
            Left = 20,
            Top = 90,
            Width = 440,
            Height = 20
        };

        trackLabel = new Label
        {
            Text = "Музыка не обнаружена",
            Left = 20,
            Top = 115,
            Width = 465,
            Height = 25,
            AutoEllipsis = true
        };

        var prefixTitle = new Label
        {
            Text = "Фраза перед треком",
            Left = 20,
            Top = 155,
            Width = 200,
            Height = 20
        };

        prefixValueLabel = new Label
        {
            Text = Settings.BioPrefix,
            Left = 20,
            Top = 182,
            Width = 330,
            Height = 28,
            AutoEllipsis = true
        };

        editPrefixButton = new Button
        {
            Text = "Изменить",
            Left = 365,
            Top = 175,
            Width = 120,
            Height = 32
        };

        prefixTextBox = new TextBox
        {
            Left = 20,
            Top = 178,
            Width = 275,
            Text = Settings.BioPrefix,
            Visible = false
        };

        applyPrefixButton = new Button
        {
            Text = "Применить",
            Left = 305,
            Top = 175,
            Width = 85,
            Height = 32,
            Visible = false
        };

        cancelPrefixButton = new Button
        {
            Text = "Отмена",
            Left = 400,
            Top = 175,
            Width = 85,
            Height = 32,
            Visible = false
        };

        editPrefixButton.Click += (_, _) =>
        {
            BeginPrefixEdit();
        };

        applyPrefixButton.Click += (_, _) =>
        {
            ApplyPrefixEdit();
        };

        cancelPrefixButton.Click += (_, _) =>
        {
            CancelPrefixEdit();
        };

        prefixTextBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                ApplyPrefixEdit();
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                CancelPrefixEdit();
            }
        };

        syncCheckBox = new CheckBox
        {
            Left = 20,
            Top = 230,
            Width = 300,
            Height = 25,
            Text = "Синхронизация включена",
            Checked = Settings.SyncEnabled
        };

        startWithWindowsCheckBox = new CheckBox
        {
            Left = 20,
            Top = 260,
            Width = 300,
            Height = 25,
            Text = "Запускать вместе с Windows",
            Checked = Settings.StartWithWindows
        };

        var authorLabel = new Label
        {
            Text = "created by s0ra",
            Left = 20,
            Top = 325,
            Width = 150,
            Height = 20,
            ForeColor = Color.Gray
        };

        saveButton = new Button
        {
            Left = 365,
            Top = 310,
            Width = 120,
            Height = 35,
            Text = "Сохранить"
        };

        saveButton.Click += (_, _) =>
        {
            if (prefixTextBox.Visible)
                ApplyPrefixEdit();

            Settings.SyncEnabled = syncCheckBox.Checked;
            Settings.StartWithWindows = startWithWindowsCheckBox.Checked;

            SettingsSaved?.Invoke();

            HideToTray();
        };

        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized)
                HideToTray();
        };

        Controls.Add(telegramTitle);
        Controls.Add(telegramStatusLabel);
        Controls.Add(telegramButton);

        Controls.Add(trackTitle);
        Controls.Add(trackLabel);

        Controls.Add(prefixTitle);
        Controls.Add(prefixValueLabel);
        Controls.Add(editPrefixButton);
        Controls.Add(prefixTextBox);
        Controls.Add(applyPrefixButton);
        Controls.Add(cancelPrefixButton);

        Controls.Add(syncCheckBox);
        Controls.Add(startWithWindowsCheckBox);

        Controls.Add(authorLabel);
        Controls.Add(saveButton);
    }

    public void SetCurrentTrack(string? track)
    {
        if (InvokeRequired)
        {
            Invoke(() => SetCurrentTrack(track));
            return;
        }

        trackLabel.Text = string.IsNullOrWhiteSpace(track)
            ? "Музыка не обнаружена"
            : track;
    }

    public void SetTelegramAccount(string? accountName)
    {
        if (InvokeRequired)
        {
            Invoke(() => SetTelegramAccount(accountName));
            return;
        }

        telegramConnected = !string.IsNullOrWhiteSpace(accountName);

        if (telegramConnected)
        {
            telegramStatusLabel.Text = $"Подключён как {accountName}";
            telegramButton.Text = "Выйти";
        }
        else
        {
            telegramStatusLabel.Text = "Не подключён";
            telegramButton.Text = "Войти в Telegram";
        }
    }

    public void RestoreWindow()
    {
        if (InvokeRequired)
        {
            Invoke(RestoreWindow);
            return;
        }

        ShowInTaskbar = true;

        if (!Visible)
            Show();

        WindowState = FormWindowState.Normal;

        Activate();
        BringToFront();
    }

    private void BeginPrefixEdit()
    {
        prefixBeforeEdit = Settings.BioPrefix;

        prefixTextBox.Text = Settings.BioPrefix;

        prefixValueLabel.Visible = false;
        editPrefixButton.Visible = false;

        prefixTextBox.Visible = true;
        applyPrefixButton.Visible = true;
        cancelPrefixButton.Visible = true;

        prefixTextBox.Focus();
        prefixTextBox.SelectAll();
    }

    private void ApplyPrefixEdit()
    {
        Settings.BioPrefix = prefixTextBox.Text;

        prefixValueLabel.Text = Settings.BioPrefix;

        EndPrefixEdit();

        SettingsSaved?.Invoke();
    }

    private void CancelPrefixEdit()
    {
        prefixTextBox.Text = prefixBeforeEdit;
        prefixValueLabel.Text = prefixBeforeEdit;

        EndPrefixEdit();
    }

    private void EndPrefixEdit()
    {
        prefixTextBox.Visible = false;
        applyPrefixButton.Visible = false;
        cancelPrefixButton.Visible = false;

        prefixValueLabel.Visible = true;
        editPrefixButton.Visible = true;
    }

    private void HideToTray()
    {
        if (InvokeRequired)
        {
            Invoke(HideToTray);
            return;
        }

        ShowInTaskbar = false;
        Hide();

        HiddenToTray?.Invoke();
    }
}