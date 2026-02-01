namespace TsukiAI.Desktop.Services;

/// <summary>Only speak every X minutes unless something meaningful happens. Prevents annoyance.</summary>
public sealed class CooldownService
{
    private DateTimeOffset _lastSpokeAt = DateTimeOffset.MinValue;
    private readonly Dictionary<string, int> _cooldownsSeconds;

    public CooldownService()
    {
        _cooldownsSeconds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["default"] = 300,
            ["frustrated"] = 180,
            ["sleepy"] = 600,
            ["bored"] = 420,
            ["focused"] = 360,
            ["idle"] = 300,
            ["happy"] = 240,
            ["concerned"] = 300
        };
    }

    /// <summary>Cooldown in seconds for the given emotion. Default 300.</summary>
    public int GetCooldownSeconds(string emotion)
    {
        var e = (emotion ?? "").Trim().ToLowerInvariant();
        return _cooldownsSeconds.TryGetValue(e, out var sec) ? sec : _cooldownsSeconds["default"];
    }

    /// <summary>True if we're allowed to speak (cooldown elapsed or meaningful event).</summary>
    public bool CanSpeak(string emotion, bool meaningfulEvent = false)
    {
        if (meaningfulEvent) return true;
        var sec = GetCooldownSeconds(emotion);
        var elapsed = (DateTimeOffset.Now - _lastSpokeAt).TotalSeconds;
        return elapsed >= sec;
    }

    /// <summary>Call after we spoke (reaction or summary comment).</summary>
    public void RecordSpoke()
    {
        _lastSpokeAt = DateTimeOffset.Now;
    }

    public void SetCooldown(string emotion, int seconds)
    {
        var e = (emotion ?? "").Trim().ToLowerInvariant();
        if (e.Length > 0)
            _cooldownsSeconds[e] = Math.Max(60, seconds);
    }
}
