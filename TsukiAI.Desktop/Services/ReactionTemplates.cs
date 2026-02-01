using System.IO;
using System.Text.Json;

namespace TsukiAI.Desktop.Services;

/// <summary>Pre-written lines for common situations. Skip LLM for speed + less CPU.</summary>
public sealed class ReactionTemplates
{
    private readonly List<ReactionTemplateEntry> _entries = new();
    private readonly string _path;
    private readonly object _lock = new();

    public ReactionTemplates(string baseDir)
    {
        _path = Path.Combine(baseDir, "reaction_templates.json");
        Load();
    }

    /// <summary>Try to get a random line for this event + mood. Returns null if no match (use LLM).</summary>
    public string? TryGetLine(string eventType, string mood)
    {
        lock (_lock)
        {
            var e = (eventType ?? "").Trim().ToLowerInvariant();
            var m = (mood ?? "").Trim().ToLowerInvariant();
            var match = _entries.FirstOrDefault(x =>
                x.Event.Equals(e, StringComparison.OrdinalIgnoreCase) &&
                x.Mood.Equals(m, StringComparison.OrdinalIgnoreCase));
            if (match?.Lines is not { Count: > 0 }) return null;
            var i = Random.Shared.Next(match.Lines.Count);
            return match.Lines[i];
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                SeedDefaults();
                return;
            }
            var json = File.ReadAllText(_path);
            var list = JsonSerializer.Deserialize<List<ReactionTemplateEntry>>(json);
            if (list != null)
            {
                lock (_lock) _entries.Clear();
                lock (_lock) _entries.AddRange(list);
            }
        }
        catch
        {
            SeedDefaults();
        }
    }

    private void SeedDefaults()
    {
        lock (_lock)
        {
            _entries.Clear();
            _entries.Add(new ReactionTemplateEntry
            {
                Event = "long_coding_session",
                Mood = "playful",
                Lines =
                [
                    "Still coding, huh? Your keyboard's gonna file a complaint.",
                    "You've been focused for a while… proud of you, idiot."
                ]
            });
            _entries.Add(new ReactionTemplateEntry
            {
                Event = "long_coding_session",
                Mood = "focused",
                Lines = ["Deep in the zone. Don't forget to blink."]
            });
            _entries.Add(new ReactionTemplateEntry
            {
                Event = "idle_long",
                Mood = "bored",
                Lines = ["You've been quiet. Everything okay?"]
            });
            _entries.Add(new ReactionTemplateEntry
            {
                Event = "switching_frequently",
                Mood = "playful",
                Lines = ["Bouncing between tabs like a pinball. Need a break?"]
            });
            Save();
        }
    }

    private void Save()
    {
        try
        {
            lock (_lock)
            {
                var json = JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_path, json);
            }
        }
        catch { }
    }

    public class ReactionTemplateEntry
    {
        public string Event { get; set; } = "";
        public string Mood { get; set; } = "";
        public List<string> Lines { get; set; } = [];
    }
}
