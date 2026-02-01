using System.IO;
using System.Text.Json;
using PersonalAiOverlay.App.Models;

namespace PersonalAiOverlay.App.Services.Logging;

public sealed class ActivitySampleStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public ActivitySampleStore()
    {
        ActivityLogPaths.EnsureDirs();
    }

    public string GetSampleBaseName(DateTimeOffset ts)
        => $"sample-{ts:yyyyMMdd-HHmmss}";

    public string GetSampleJsonPath(string baseName)
        => Path.Combine(ActivityLogPaths.RawDir, baseName + ".json");

    public string GetSampleScreenshotPath(string baseName)
        => Path.Combine(ActivityLogPaths.RawDir, baseName + ".png");

    public void SaveSample(ActivitySample sample, string jsonPath)
    {
        var json = JsonSerializer.Serialize(sample, JsonOptions);
        File.WriteAllText(jsonPath, json);
    }

    public List<string> ListSampleJsonFilesNewestFirst()
    {
        if (!Directory.Exists(ActivityLogPaths.RawDir))
            return [];

        return Directory.GetFiles(ActivityLogPaths.RawDir, "sample-*.json")
            .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public ActivitySample? TryLoadSample(string jsonPath)
    {
        try
        {
            var json = File.ReadAllText(jsonPath);
            return JsonSerializer.Deserialize<ActivitySample>(json);
        }
        catch
        {
            return null;
        }
    }

    public void DeleteSampleAndAssets(ActivitySample sample)
    {
        try
        {
            var baseName = GetSampleBaseName(sample.Timestamp);
            var jsonPath = GetSampleJsonPath(baseName);
            if (File.Exists(jsonPath)) File.Delete(jsonPath);
        }
        catch { }

        try
        {
            if (!string.IsNullOrWhiteSpace(sample.ScreenshotPath) && File.Exists(sample.ScreenshotPath))
                File.Delete(sample.ScreenshotPath);
        }
        catch { }
    }

    public void CleanupRawOlderThan(TimeSpan maxAge)
    {
        try
        {
            if (!Directory.Exists(ActivityLogPaths.RawDir))
                return;

            var cutoff = DateTimeOffset.Now - maxAge;
            foreach (var jsonPath in Directory.GetFiles(ActivityLogPaths.RawDir, "sample-*.json"))
            {
                var sample = TryLoadSample(jsonPath);
                if (sample is null)
                {
                    // If we can't parse it, keep it (we'll avoid destructive deletes).
                    continue;
                }

                if (sample.Timestamp < cutoff)
                    DeleteSampleAndAssets(sample);
            }
        }
        catch
        {
            // ignore
        }
    }
}

