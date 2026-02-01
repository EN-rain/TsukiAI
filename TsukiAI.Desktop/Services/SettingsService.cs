using System.IO;
using System.Text.Json;
using TsukiAI.Desktop.Models;

namespace TsukiAI.Desktop.Services;

public static class SettingsService
{
    private const string FileName = "settings.json";

    public static string GetBaseDir()
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PersonalAiOverlay"
        );
        Directory.CreateDirectory(baseDir);
        return baseDir;
    }

    public static string GetSettingsPath()
    {
        return Path.Combine(GetBaseDir(), FileName);
    }

    public static AppSettings Load()
    {
        try
        {
            var path = GetSettingsPath();
            if (!File.Exists(path))
                return AppSettings.Default;

            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json);
            return settings ?? AppSettings.Default;
        }
        catch
        {
            return AppSettings.Default;
        }
    }

    public static void Save(AppSettings settings)
    {
        settings ??= AppSettings.Default;
        var path = GetSettingsPath();
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }
}

