namespace TsukiAI.Desktop.Models;

public enum ScreenshotCaptureMode
{
    FullScreen = 0,
    ActiveWindow = 1,
}

public sealed record AppSettings(
    bool IsActivityLoggingEnabled = false,
    int SampleIntervalMinutes = 5,
    int SummarizeIntervalMinutes = 60,
    ScreenshotCaptureMode CaptureMode = ScreenshotCaptureMode.FullScreen,
    int RetentionHoursForRaw = 1,
    string ModelName = "qwen2.5:3b",
    bool AutoStartOllama = true,
    bool StopOllamaOnExit = true,
    bool StartupGreetingEnabled = true,
    bool ProactiveMessagesEnabled = true,
    int ProactiveMessageAfterMinutes = 10,
    bool UseGpu = true,
    string ModelDirectory = ""
)
{
    public static AppSettings Default => new();
}

