using System.IO;
using System.Linq;
using TsukiAI.Desktop.Services.Logging;

namespace TsukiAI.Desktop.Services;

public sealed class MemoryLearner
{
    private readonly MemoryStore _memoryStore;
    private readonly OllamaClient _ollama;

    public MemoryLearner(MemoryStore memoryStore, OllamaClient ollama)
    {
        _memoryStore = memoryStore;
        _ollama = ollama;
    }

    public async Task RunDailyLearningAsync(CancellationToken ct)
    {
        var data = _memoryStore.GetData();
        var last = data.Meta.LastLearningAt ?? DateTimeOffset.MinValue;
        if ((DateTimeOffset.Now - last) < TimeSpan.FromHours(20))
            return;

        ActivityLogPaths.EnsureDirs();
        if (!Directory.Exists(ActivityLogPaths.SummariesDir))
            return;

        var files = Directory.GetFiles(ActivityLogPaths.SummariesDir, "summary-*.md")
            .OrderByDescending(f => f)
            .Take(24)
            .ToList();

        if (files.Count == 0)
            return;

        var combined = string.Join("\n\n", files.Select(File.ReadAllText));
        var system =
            """
You are Tsuki, a local AI companion.
Summarize the user's recent activity into 3-6 bullet points of learning-worthy patterns.
Keep it short and practical.
""";

        var user = "HourlySummaries:\n" + combined;
        var text = await _ollama.PostForTextAsync(system, user, ct);
        if (!string.IsNullOrWhiteSpace(text))
        {
            _memoryStore.AddLearningSummary(text.Trim(), "daily");
            _memoryStore.UpdateLearningAt(DateTimeOffset.Now);
        }
    }

    public async Task RunWeeklyLearningAsync(CancellationToken ct)
    {
        var data = _memoryStore.GetData();
        var last = data.Meta.LastWeeklyLearningAt ?? DateTimeOffset.MinValue;
        if ((DateTimeOffset.Now - last) < TimeSpan.FromDays(6))
            return;

        ActivityLogPaths.EnsureDirs();
        if (!Directory.Exists(ActivityLogPaths.SummariesDir))
            return;

        var files = Directory.GetFiles(ActivityLogPaths.SummariesDir, "summary-*.md")
            .OrderByDescending(f => f)
            .Take(7 * 24)
            .ToList();

        if (files.Count == 0)
            return;

        var combined = string.Join("\n\n", files.Select(File.ReadAllText));
        var system =
            """
You are Tsuki, a local AI companion.
Create a weekly learning summary in 4-8 bullet points.
Highlight patterns, goals, and repeated themes.
""";

        var user = "HourlySummaries:\n" + combined;
        var text = await _ollama.PostForTextAsync(system, user, ct);
        if (!string.IsNullOrWhiteSpace(text))
        {
            _memoryStore.AddLearningSummary(text.Trim(), "weekly");
            _memoryStore.UpdateWeeklyLearningAt(DateTimeOffset.Now);
        }
    }
}

