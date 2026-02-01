using System.IO;
using TsukiAI.Desktop.Services;

namespace TsukiAI.Desktop.Services.Logging;

public static class ActivityLogPaths
{
    public static string LogsRoot => Path.Combine(SettingsService.GetBaseDir(), "logs");
    public static string RawDir => Path.Combine(LogsRoot, "raw");
    public static string SummariesDir => Path.Combine(LogsRoot, "summaries");

    public static void EnsureDirs()
    {
        Directory.CreateDirectory(RawDir);
        Directory.CreateDirectory(SummariesDir);
    }
}

