using TsukiAI.Desktop.Models;

namespace TsukiAI.Desktop.Services;

/// <summary>Automatic emotion from activity. States: idle, focused, frustrated, happy, sleepy, bored, concerned.</summary>
public sealed class EmotionStateMachine
{
    private const int IdleMinutesBored = 15;
    private const int CodingMinutesFocused = 60;
    private const int LateNightStart = 1;
    private const int LateNightEnd = 4;

    private static readonly HashSet<string> CodingProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "code", "devenv", "msbuild", "dotnet", "cursor", "visual studio",
        "vscode", "idea64", "pycharm", "webstorm", "rider"
    };

    public string CurrentEmotion { get; private set; } = "idle";

    /// <summary>Update emotion from recent activity. Call from sample capture or before building prompts.</summary>
    public void Update(
        IReadOnlyList<ActivitySample> recentSamples,
        int currentIdleSeconds,
        int recentErrorOrCrashCount = 0)
    {
        var hour = DateTime.Now.Hour;
        var idleMinutes = currentIdleSeconds / 60.0;

        // Late night 1–4 AM → sleepy
        if (hour >= LateNightStart && hour <= LateNightEnd)
        {
            CurrentEmotion = "sleepy";
            return;
        }

        // App crash / repeated errors → frustrated
        if (recentErrorOrCrashCount >= 2)
        {
            CurrentEmotion = "frustrated";
            return;
        }

        // Idle > 15 min → bored
        if (idleMinutes >= IdleMinutesBored)
        {
            CurrentEmotion = "bored";
            return;
        }

        // Coding > 60 min (in recent window) → focused
        var codingMinutes = GetCodingMinutes(recentSamples);
        if (codingMinutes >= CodingMinutesFocused)
        {
            CurrentEmotion = "focused";
            return;
        }

        // Default: idle if no strong signal
        if (recentSamples.Count == 0)
        {
            CurrentEmotion = "idle";
            return;
        }

        // Light activity: stay idle or happy if short session
        CurrentEmotion = codingMinutes >= 10 ? "focused" : "idle";
    }

    /// <summary>Set emotion explicitly (e.g. from user message or LLM reply).</summary>
    public void SetEmotion(string emotion)
    {
        var e = (emotion ?? "").Trim().ToLowerInvariant();
        if (e.Length > 0)
            CurrentEmotion = e;
    }

    private static double GetCodingMinutes(IReadOnlyList<ActivitySample> samples)
    {
        if (samples.Count == 0) return 0;
        var intervalMinutes = 5.0; // assume ~5 min between samples if from activity logger
        var codingCount = 0;
        foreach (var s in samples)
        {
            var proc = (s.ProcessName ?? "").ToLowerInvariant();
            var title = (s.WindowTitle ?? "").ToLowerInvariant();
            if (CodingProcesses.Any(p => proc.Contains(p) || title.Contains(p)))
                codingCount++;
        }
        return codingCount * intervalMinutes;
    }
}
