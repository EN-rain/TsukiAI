using TsukiAI.Desktop.Models;

namespace TsukiAI.Desktop.Services;

public sealed class MemoryRules
{
    private static readonly string[] ExplicitPhrases =
    [
        "remember this",
        "remember that",
        "save this",
        "note this",
        "keep this in mind"
    ];

    public bool TryExtract(string userText, out MemoryEntry? entry)
    {
        entry = null;
        var text = (userText ?? "").Trim();
        if (text.Length == 0) return false;

        if (TryExplicit(text, out entry))
            return true;

        if (TryAutoLowRisk(text, out entry))
            return true;

        return false;
    }

    private bool TryExplicit(string text, out MemoryEntry? entry)
    {
        entry = null;
        var lowered = text.ToLowerInvariant();
        if (!ExplicitPhrases.Any(p => lowered.Contains(p)))
            return false;

        foreach (var phrase in ExplicitPhrases)
            lowered = lowered.Replace(phrase, "");

        var content = text;
        if (lowered.Length > 0)
            content = lowered.Trim();

        entry = MakeEntry("explicit", content, "explicit", 4, confidence: 1.0);
        return true;
    }

    private bool TryAutoLowRisk(string text, out MemoryEntry? entry)
    {
        entry = null;
        var lowered = text.ToLowerInvariant();

        if (lowered.Contains("i like ") || lowered.Contains("i love ") || lowered.Contains("i prefer "))
        {
            entry = MakeEntry("preference", text, "auto_low", 2, confidence: 0.5, "preference");
            return true;
        }

        if (lowered.Contains("i'm working on") || lowered.Contains("i am working on") || lowered.Contains("my project"))
        {
            entry = MakeEntry("project", text, "auto_low", 3, confidence: 0.5, "project");
            return true;
        }

        if (lowered.Contains("i work best") || lowered.Contains("i usually") || lowered.Contains("i tend to"))
        {
            entry = MakeEntry("workstyle", text, "auto_low", 2, confidence: 0.5, "workstyle");
            return true;
        }

        if (lowered.StartsWith("my ") && lowered.Contains(" is "))
        {
            entry = MakeEntry("fact", text, "auto_low", 2, confidence: 0.5, "fact");
            return true;
        }

        return false;
    }

    private static MemoryEntry MakeEntry(string type, string content, string source, int importance, double confidence = 1.0, params string[] tags)
    {
        content = (content ?? "").Trim();
        if (content.Length > 240) content = content[..240] + "…";

        return new MemoryEntry(
            Id: Guid.NewGuid().ToString("N"),
            Type: type,
            Content: content,
            Tags: tags.Length == 0 ? [type] : tags,
            Importance: importance,
            CreatedAt: DateTimeOffset.Now,
            LastUsedAt: null,
            DecayScore: 0,
            Source: source
        ) { Confidence = confidence, SeenCount = 1 };
    }
}

