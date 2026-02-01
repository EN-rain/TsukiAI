namespace TsukiAI.Desktop.Models;

public sealed record ActivitySample(
    DateTimeOffset Timestamp,
    string ProcessName,
    string WindowTitle,
    int IdleSeconds,
    string? ScreenshotPath
);

