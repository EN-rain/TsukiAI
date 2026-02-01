using System.Text.Json;
using System.IO;
using System.Linq;
using PersonalAiOverlay.App.Models;

namespace PersonalAiOverlay.App.Services;

public sealed class MemoryStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private MemoryData _data = new();

    public MemoryStore(string baseDir)
    {
        Directory.CreateDirectory(baseDir);
        _path = Path.Combine(baseDir, "memory.json");
        Load();
    }

    public MemoryData GetData()
    {
        lock (_lock) return _data;
    }

    public IReadOnlyList<MemoryEntry> GetEntries()
    {
        lock (_lock) return _data.Entries.ToList();
    }

    public void AddOrUpdate(MemoryEntry entry)
    {
        lock (_lock)
        {
            var existing = _data.Entries.FirstOrDefault(e => e.Content.Equals(entry.Content, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                _data.Entries.Remove(existing);
                var newSeen = existing.SeenCount + 1;
                var confidence = Math.Min(1.0, 0.5 + newSeen * 0.2);
                entry = existing with { SeenCount = newSeen, Confidence = confidence };
            }
            _data.Entries.Add(entry);
            Save();
        }
    }

    public List<MemoryEntry> GetRelevant(string input, int max = 5)
    {
        input = (input ?? "").ToLowerInvariant();
        if (input.Length == 0) return [];

        var tokens = Tokenize(input).ToHashSet();
        lock (_lock)
        {
            var scored = _data.Entries
                .Where(e => e.Confidence >= 0.8)
                .Select(e =>
                {
                    var score = e.Importance * 2 - e.DecayScore;
                    if (e.LastUsedAt.HasValue)
                    {
                        var days = (DateTimeOffset.Now - e.LastUsedAt.Value).TotalDays;
                        score += 1.0 / (1.0 + days);
                    }

                    var contentTokens = Tokenize(e.Content.ToLowerInvariant());
                    var overlap = contentTokens.Count(t => tokens.Contains(t));
                    score += overlap * 0.5;

                    return (entry: e, score);
                })
                .OrderByDescending(x => x.score)
                .Take(max)
                .Select(x => x.entry)
                .ToList();

            foreach (var e in scored)
                Touch(e.Id);

            return scored;
        }
    }

    public void Touch(string id)
    {
        var idx = _data.Entries.FindIndex(e => e.Id == id);
        if (idx < 0) return;
        var e = _data.Entries[idx];
        _data.Entries[idx] = e with { LastUsedAt = DateTimeOffset.Now };
        Save();
    }

    public void ApplyDecay()
    {
        lock (_lock)
        {
            var now = DateTimeOffset.Now;
            var last = _data.Meta.LastDecayAt ?? now;
            var days = Math.Max(0, (now - last).TotalDays);

            if (days < 0.5) return;

            for (var i = 0; i < _data.Entries.Count; i++)
            {
                var e = _data.Entries[i];
                var newScore = e.DecayScore + days * 0.2;
                var newImportance = e.Importance;
                if (newScore > 5 && newImportance > 1) newImportance -= 1;
                _data.Entries[i] = e with { DecayScore = newScore, Importance = newImportance };
            }

            _data.Meta.LastDecayAt = now;
            Save();
        }
    }

    public void AddLearningSummary(string content, string source = "daily")
    {
        var entry = new MemoryEntry(
            Id: Guid.NewGuid().ToString("N"),
            Type: "learning",
            Content: content.Trim(),
            Tags: ["learning"],
            Importance: 3,
            CreatedAt: DateTimeOffset.Now,
            LastUsedAt: null,
            DecayScore: 0,
            Source: source
        );
        AddOrUpdate(entry);
    }

    public void UpdateLearningAt(DateTimeOffset at)
    {
        lock (_lock)
        {
            _data.Meta.LastLearningAt = at;
            Save();
        }
    }

    public void UpdateWeeklyLearningAt(DateTimeOffset at)
    {
        lock (_lock)
        {
            _data.Meta.LastWeeklyLearningAt = at;
            Save();
        }
    }

    private void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_path))
            {
                _data = new MemoryData();
                Save();
                return;
            }

            try
            {
                var json = File.ReadAllText(_path);
                _data = JsonSerializer.Deserialize<MemoryData>(json) ?? new MemoryData();
            }
            catch
            {
                _data = new MemoryData();
            }
        }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_path, json);
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var parts = text.Split(new[] { ' ', '\t', '\r', '\n', ',', '.', ':', ';', '!', '?', '(', ')', '[', ']' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts)
        {
            var t = p.Trim();
            if (t.Length <= 2) continue;
            yield return t;
        }
    }
}

