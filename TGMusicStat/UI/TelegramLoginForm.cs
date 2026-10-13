using TGMusicStat.Telegram;

namespace TGMusicStat.UI;

public class TelegramLoginForm : Form
{
    private readonly TelegramClientService telegram;

    private readonly Label titleLabel;
    private readonly Label descriptionLabel;
    private readonly TextBox inputTextBox;
    private readonly Label errorLabel;
    private readonly Button continueButton;
    private readonly Button cancelButton;

    private string? currentRequirement;

    public TelegramLoginForm(TelegramClientService telegram)
    {
        this.telegram = telegram;

        Text = "Вход в Telegram";
        Width = 420;
        Height = 260;

        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;

        MaximizeBox = false;
        MinimizeBox = false;

        var executablePath = Environment.ProcessPath;

        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            var icon = Icon.ExtractAssociatedIcon(executablePath);

            if (icon != null)
                Icon = icon;
        }

        titleLabel = new Label
        {
            Text = "Авторизация Telegram",
            Left = 20,
            Top = 20,
            Width = 360,
            Height = 25,
            Font = new Font(Font, FontStyle.Bold)
        };

        descriptionLabel = new Label
        {
            Text = "Введите номер телефона",
            Left = 20,
            Top = 55,
            Width = 360,
            Height = 20
        };

        inputTextBox = new TextBox
        {
            Left = 20,
            Top = 82,
            Width = 360,
            Text = "+7"
        };

        errorLabel = new Label
        {
            Left = 20,
            Top = 115,
            Width = 360,
            Height = 40,
            ForeColor = Color.Firebrick
        };

        continueButton = new Button
        {
            Text = "Продолжить",
            Left = 250,
            Top = 165,
            Width = 130,
            Height = 32
        };

        cancelButton = new Button
        {
            Text = "Отмена",
            Left = 145,
            Top = 165,
            Width = 95,
            Height = 32
        };

        continueButton.Click += async (_, _) =>
        {
            await ContinueAsync();
        };

        cancelButton.Click += async (_, _) =>
        {
            await telegram.CancelLoginAsync();

            DialogResult = DialogResult.Cancel;
            Close();
        };

        inputTextBox.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter)
                return;

            e.SuppressKeyPress = true;

            await ContinueAsync();
        };

        Controls.Add(titleLabel);
        Controls.Add(descriptionLabel);
        Controls.Add(inputTextBox);
        Controls.Add(errorLabel);
        Controls.Add(continueButton);
        Controls.Add(cancelButton);
    }

    private async Task ContinueAsync()
    {
        var value = inputTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(value))
        {
            errorLabel.Text = "Поле не должно быть пустым.";
            return;
        }

        SetLoading(true);

        try
        {
            string? requirement;

            if (currentRequirement == null)
                requirement = await telegram.BeginLoginAsync(value);
            else
                requirement = await telegram.ContinueLoginAsync(value);

            if (telegram.IsLoggedIn)
            {
                DialogResult = DialogResult.OK;
                Close();

                return;
            }

            currentRequirement = requirement;

            ConfigureRequirement(requirement);
        }
        catch (Exception ex)
        {
            errorLabel.Text = GetFriendlyError(ex.Message);
        }
        finally
        {
            SetLoading(false);
        }
    }

    private void ConfigureRequirement(string? requirement)
    {
        errorLabel.Text = "";
        inputTextBox.Text = "";
        inputTextBox.UseSystemPasswordChar = false;

        switch (requirement)
        {
            case "verification_code":
                descriptionLabel.Text = "Введите код, который прислал Telegram";
                break;

            case "password":
                descriptionLabel.Text = "Введите пароль двухэтапной аутентификации";
                inputTextBox.UseSystemPasswordChar = true;
                break;

            case "name":
                descriptionLabel.Text = "Введите имя и фамилию";
                break;

            case "email":
                descriptionLabel.Text = "Введите email";
                break;

            case "email_verification_code":
                descriptionLabel.Text = "Введите код, отправленный на email";
                break;

            default:
                descriptionLabel.Text =
                    $"Telegram запрашивает: {requirement ?? "данные"}";
                break;
        }

        inputTextBox.Focus();
    }

    private void SetLoading(bool loading)
    {
        continueButton.Enabled = !loading;
        cancelButton.Enabled = !loading;
        inputTextBox.Enabled = !loading;

        continueButton.Text =
            loading
                ? "Подключение..."
                : "Продолжить";
    }

    private static string GetFriendlyError(string message)
    {
        if (message.Contains(
                "PHONE_CODE_INVALID",
                StringComparison.OrdinalIgnoreCase))
        {
            return "Неверный код Telegram.";
        }

        if (message.Contains(
                "PASSWORD_HASH_INVALID",
                StringComparison.OrdinalIgnoreCase))
        {
            return "Неверный пароль 2FA.";
        }

        if (message.Contains(
                "PHONE_NUMBER_INVALID",
                StringComparison.OrdinalIgnoreCase))
        {
            return "Неверный номер телефона.";
        }

        if (message.Contains(
                "FLOOD",
                StringComparison.OrdinalIgnoreCase))
        {
            return "Слишком много попыток. Telegram временно ограничил вход.";
        }

        return message;
    }
}