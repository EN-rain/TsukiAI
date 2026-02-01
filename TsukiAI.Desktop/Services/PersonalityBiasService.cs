using System.IO;
using System.Text.Json;

namespace TsukiAI.Desktop.Services;

/// <summary>Personality drift: slow change over time. teasing, supportive, sarcastic (0–1).</summary>
public sealed class PersonalityBiasService
{
    private const double Delta = 0.02;
    private const string FileName = "personality_bias.json";

    private readonly string _path;
    private readonly object _lock = new();
    private PersonalityBias _bias = new();

    public PersonalityBiasService(string baseDir)
    {
        _path = Path.Combine(baseDir, FileName);
        Load();
    }

    public double Teasing => _bias.Teasing;
    public double Supportive => _bias.Supportive;
    public double Sarcastic => _bias.Sarcastic;

    /// <summary>Get a short line for the prompt (e.g. "Slightly more teasing, very supportive.").</summary>
    public string GetPromptHint()
    {
        lock (_lock)
        {
            var parts = new List<string>();
            if (_bias.Teasing >= 0.6) parts.Add("light teasing");
            else if (_bias.Teasing <= 0.4) parts.Add("minimal teasing");
            if (_bias.Supportive >= 0.7) parts.Add("very supportive");
            else if (_bias.Supportive <= 0.5) parts.Add("neutral support");
            if (_bias.Sarcastic >= 0.5) parts.Add("slightly sarcastic");
            if (parts.Count == 0) return "Balanced tone.";
            return string.Join(", ", parts) + ".";
        }
    }

    /// <summary>Call when user responded positively (e.g. continued chat). Increase supportive/playful.</summary>
    public void OnPositiveResponse()
    {
        lock (_lock)
        {
            _bias.Supportive = Clamp(_bias.Supportive + Delta);
            _bias.Teasing = Clamp(_bias.Teasing + Delta * 0.5);
            Save();
        }
    }

    /// <summary>Call when user ignored jokes or seemed annoyed. Reduce teasing.</summary>
    public void OnNegativeOrIgnore()
    {
        lock (_lock)
        {
            _bias.Teasing = Clamp(_bias.Teasing - Delta);
            _bias.Sarcastic = Clamp(_bias.Sarcastic - Delta);
            Save();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize<PersonalityBias>(json);
            if (loaded != null)
                lock (_lock) _bias = loaded;
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_bias, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json);
        }
        catch { }
    }

    private static double Clamp(double v) => Math.Clamp(v, 0, 1);

    public class PersonalityBias
    {
        public double Teasing { get; set; } = 0.6;
        public double Supportive { get; set; } = 0.8;
        public double Sarcastic { get; set; } = 0.4;
    }
}
