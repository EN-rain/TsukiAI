using TsukiAI.Desktop.Models;

namespace TsukiAI.Desktop.Services;

/// <summary>Summarize raw activity into one short, high-level line. Never send raw exe/tabs to the model.</summary>
public static class EventSummarizer
{
    private static readonly HashSet<string> CodeKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "code", "vs", "visual studio", "cursor", "devenv", "vscode", "idea", "pycharm", "rider"
    };
    private static readonly HashSet<string> BrowserKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "firefox", "edge", "browser", "msedge"
    };
    private static readonly HashSet<string> TerminalKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "terminal", "wsl", "wt"
    };

    /// <summary>Turn recent samples into one anti-creepy summary line.</summary>
    public static string Summarize(IReadOnlyList<ActivitySample> samples)
    {
        if (samples == null || samples.Count == 0)
            return "No recent activity.";

        var categories = new List<string>();
        var prevCategory = "";
        var switches = 0;

        foreach (var s in samples)
        {
            var cat = Category(s.ProcessName, s.WindowTitle);
            if (cat.Length > 0 && cat != prevCategory)
            {
                if (prevCategory.Length > 0) switches++;
                prevCategory = cat;
                if (!categories.Contains(cat))
                    categories.Add(cat);
            }
        }

        var totalIdle = samples.Sum(x => x.IdleSeconds);
        var avgIdle = samples.Count > 0 ? totalIdle / samples.Count : 0;

        if (avgIdle >= 600) // 10+ min average idle
            return "User has been mostly idle recently.";
        if (switches >= 4 && categories.Count >= 2)
            return "User has been switching between " + string.Join(" and ", categories.Take(3)) + " frequently.";
        if (categories.Count == 1)
            return "User has been in " + categories[0] + ".";
        if (categories.Count >= 2)
            return "User has been alternating between " + string.Join(" and ", categories.Take(3)) + ".";

        return "User has been active recently.";
    }

    private static string Category(string? processName, string? windowTitle)
    {
        var p = (processName ?? "").ToLowerInvariant();
        var t = (windowTitle ?? "").ToLowerInvariant();
        if (CodeKeywords.Any(k => p.Contains(k) || t.Contains(k))) return "code";
        if (BrowserKeywords.Any(k => p.Contains(k) || t.Contains(k))) return "browser";
        if (TerminalKeywords.Any(k => p.Contains(k) || t.Contains(k))) return "terminal";
        return "other";
    }
}
