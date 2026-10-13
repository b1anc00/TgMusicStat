using Microsoft.Win32;

namespace TGMusicStat.Settings;

public static class AutoStartManager
{
    private const string RegistryPath =
        @"Software\Microsoft\Windows\CurrentVersion\Run";

    private const string AppName = "TGMusicStat";

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            RegistryPath,
            writable: true);

        if (key == null)
            return;

        if (!enabled)
        {
            key.DeleteValue(AppName, false);
            return;
        }

        var executablePath = Environment.ProcessPath;

        if (string.IsNullOrWhiteSpace(executablePath))
            return;

        var value = $"\"{executablePath}\" --startup";

        key.SetValue(AppName, value);
    }

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);

        return key?.GetValue(AppName) != null;
    }
}