using System.Text.Json;

namespace TGMusicStat.Settings;

public class SettingsStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TGMusicStat");

    private static readonly string FilePath = Path.Combine(
        DirectoryPath,
        "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new AppSettings();

            var json = File.ReadAllText(FilePath);

            return JsonSerializer.Deserialize<AppSettings>(json)
                   ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);

        var json = JsonSerializer.Serialize(
            settings,
            new JsonSerializerOptions
            {
                WriteIndented = true
            });

        File.WriteAllText(FilePath, json);
    }
}