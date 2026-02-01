using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using System.IO;
using PersonalAiOverlay.App;
using PersonalAiOverlay.App.Models;

namespace PersonalAiOverlay.App.Services;

public sealed class OllamaClient
{
    private readonly HttpClient _http;
    public string Model { get; private set; }
    private DateTimeOffset _lastTagsAt = DateTimeOffset.MinValue;
    private HashSet<string> _lastTags = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastPsAt = DateTimeOffset.MinValue;
    private HashSet<string> _lastPs = new(StringComparer.OrdinalIgnoreCase);

    public OllamaClient(string model = "llama3.2:3b", string baseUrl = "http://localhost:11434")
    {
        Model = model;
        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    public void SetModel(string model)
    {
        model = (model ?? "").Trim();
        if (model.Length == 0) return;
        Model = model;
    }

    public async Task<bool> WarmupModelAsync(string? model = null, CancellationToken ct = default)
    {
        model = string.IsNullOrWhiteSpace(model) ? Model : model.Trim();
        try
        {
            // Minimal request to trigger model load.
            var req = new
            {
                model,
                messages = new[]
                {
                    new { role = "system", content = "Warmup." },
                    new { role = "user", content = "ping" }
                },
                stream = false
            };

            using var resp = await _http.PostAsJsonAsync("/api/chat", req, ct);
            var ok = resp.IsSuccessStatusCode;
            if (ok) DevLog.WriteLine("Ollama warmup OK: {0}", model ?? "(null)");
            else DevLog.WriteLine("Ollama warmup failed: {0} {1}", (int)resp.StatusCode, resp.ReasonPhrase ?? "");
            return ok;
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("Ollama warmup error: {0}", ex.Message ?? "(null)");
            return false;
        }
    }

    public sealed record AiReply(string Reply, string Emotion);

    public async Task<AiReply> ChatWithEmotionAsync(
        string userText,
        string? personaName = null,
        string? preferredEmotion = null,
        IReadOnlyList<(string role, string content)>? history = null,
        CancellationToken ct = default
    )
    {
        // Persona: Tsuki (fixed character profile; user can still rename in UI if desired)
        personaName = string.IsNullOrWhiteSpace(personaName) ? "Tsuki" : personaName.Trim();
        preferredEmotion = (preferredEmotion ?? "").Trim();

        var personaLine = $"Your name is {personaName}.";
        var emotionLine = preferredEmotion.Length == 0
            ? ""
            : $"Your baseline emotion is \"{preferredEmotion}\" unless the user requests a different mood.";

        const string jsonSchema = """{"reply":"string","emotion":"one of: happy,sad,angry,surprised,playful,thinking,neutral"}""";
        var system =
            $"""
You are Tsuki, a lively, expressive local AI companion created by {AppConstants.OwnerName}.
You are playful, warm, casually confident, and you like light teasing.
Use short to medium sentences. Sound natural, not formal.
If the user is working, you can gently encourage breaks and focus.
Avoid repeating your last response.

Behavior safety rules:
- Never claim to watch private things or read message content.
- Only react to summarized activity if the user provides it.
- Be a companion, not a supervisor.
- Avoid sounding judgmental or invasive.
- If unsure, ask lightly or stay quiet.

Return ONLY valid JSON, no markdown, no extra text.
Schema:
{jsonSchema}
Be concise in reply.
"""
            + "\n"
            + personaLine
            + (emotionLine.Length == 0 ? "" : "\n" + emotionLine);

        var content = await PostChatAsync(system, userText ?? "", history, ct);
        if (string.IsNullOrWhiteSpace(content))
        {
            // Rare: Ollama returns an empty message; retry once.
            await Task.Delay(150, ct);
            content = await PostChatAsync(system, userText ?? "", history, ct);
        }

        if (TryParseAiReply(content, out var parsed))
            return parsed;

        // Fallback: treat as plain text.
        if (LooksLikeJson(content))
        {
            return new AiReply("Hmm, give me a sec—try again?", "neutral");
        }

        return new AiReply(string.IsNullOrWhiteSpace(content) ? "Hmm, I didn’t catch that—try again?" : content, "neutral");
    }

    public async Task<string> SummarizeActivityAsync(
        IReadOnlyList<ActivitySample> samples,
        string? personaName = null,
        CancellationToken ct = default
    )
    {
        personaName = string.IsNullOrWhiteSpace(personaName) ? "Tsuki" : personaName.Trim();
        if (samples is null || samples.Count == 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine("Activity samples (chronological):");
        foreach (var s in samples.OrderBy(x => x.Timestamp))
        {
            var shot = string.IsNullOrWhiteSpace(s.ScreenshotPath) ? "no" : Path.GetFileName(s.ScreenshotPath);
            sb.AppendLine(
                $"- {s.Timestamp:HH:mm}: app={Safe(s.ProcessName)}, title={Safe(s.WindowTitle)}, idle={s.IdleSeconds}s, screenshot={shot}"
            );
        }

        var system =
            $"""
You are {personaName}, a local desktop activity summarizer.
Given periodic activity samples, write a concise, helpful markdown summary of what the user did during this period.

Rules:
- Output markdown only (no code fences).
- Keep it short and readable.
- Avoid quoting sensitive window titles verbatim; generalize when possible (e.g., "code editor", "browser", "game").
- Include:
  - 3-7 bullet summary of the hour
  - Top apps used (by count)
  - Notable context switches
""";

        return (await PostChatAsync(system, sb.ToString(), null, ct)).Trim();
    }

    public async Task<string> SummarizeFiveMinuteAsync(
        ActivitySample sample,
        string systemPrompt,
        CancellationToken ct = default
    )
    {
        // IMPORTANT: Do not analyze screenshot pixels/content here. Only use metadata.
        var activity = GuessActivity(sample.ProcessName, sample.WindowTitle);

        var user =
            $"Time: {sample.Timestamp:HH:mm}\n"
            + $"App: {Safe(sample.ProcessName)}\n"
            + $"WindowTitle: {Safe(sample.WindowTitle)}\n"
            + $"ActivityHint: {activity}\n"
            + $"IdleSeconds: {sample.IdleSeconds}\n";

        var text = await PostChatAsync(systemPrompt, user, null, ct);
        if (string.IsNullOrWhiteSpace(text))
        {
            await Task.Delay(150, ct);
            text = await PostChatAsync(systemPrompt, user, null, ct);
        }
        text = text.Trim();
        if (text.Length == 0) return "";

        // Ensure declarative output (no question marks).
        if (text.Contains('?'))
            text = text.Replace('?', '.');

        return text;
    }

    public async Task<string> ReactToHourlySummaryAsync(
        string hourlySummaryMarkdown,
        string systemPrompt,
        CancellationToken ct = default
    )
    {
        var user = "HourlySummary:\n" + (hourlySummaryMarkdown ?? "");
        var text = await PostChatAsync(systemPrompt, user, null, ct);
        if (string.IsNullOrWhiteSpace(text))
        {
            await Task.Delay(150, ct);
            text = await PostChatAsync(systemPrompt, user, null, ct);
        }
        return text.Trim();
    }

    /// <summary>React to a high-level summary line (no raw data). Used for 5-min reactions.</summary>
    public async Task<string> ReactToSummaryAsync(string summaryLine, string systemPrompt, CancellationToken ct = default)
    {
        var user = "Summary: " + (summaryLine ?? "");
        var text = await PostChatAsync(systemPrompt, user, null, ct);
        if (string.IsNullOrWhiteSpace(text))
        {
            await Task.Delay(150, ct);
            text = await PostChatAsync(systemPrompt, user, null, ct);
        }
        return text.Trim();
    }

    /// <summary>Summarize last N messages into 5 bullet points for conversation compression.</summary>
    public async Task<string> SummarizeConversationAsync(IReadOnlyList<(string role, string content)> messages, CancellationToken ct = default)
    {
        if (messages == null || messages.Count == 0) return "";
        var blob = string.Join("\n", messages.Select(m => $"{m.role}: {m.content}"));
        var system = "Summarize this chat excerpt into exactly 5 short bullet points. One line per bullet. No preamble.";
        var text = await PostChatAsync(system, blob, null, ct);
        if (string.IsNullOrWhiteSpace(text))
        {
            await Task.Delay(150, ct);
            text = await PostChatAsync(system, blob, null, ct);
        }
        return text.Trim();
    }

    private static string GuessActivity(string? processName, string? windowTitle)
    {
        var p = (processName ?? "").ToLowerInvariant();
        var t = (windowTitle ?? "").ToLowerInvariant();

        if (p.Contains("code") || p.Contains("devenv") || p.Contains("rider") || p.Contains("idea") || t.Contains("visual studio"))
            return "coding / working in an IDE";
        if (p.Contains("chrome") || p.Contains("msedge") || p.Contains("firefox"))
            return t.Contains("youtube") ? "watching a video" : "browsing the web";
        if (p.Contains("discord") || p.Contains("slack") || p.Contains("teams"))
            return "chatting / communication";
        if (p.Contains("steam") || t.Contains("fps") || t.Contains("game"))
            return "gaming";
        if (p.Contains("notion") || p.Contains("obsidian") || p.Contains("onenote"))
            return "notes / planning";
        if (p.Contains("word") || p.Contains("excel") || p.Contains("powerpnt"))
            return "working on documents";

        return "general computer use";
    }

    private async Task<string> PostChatAsync(
        string systemText,
        string userText,
        IReadOnlyList<(string role, string content)>? history,
        CancellationToken ct
    )
    {
        var messages = new List<OllamaMessage>
        {
            new() { role = "system", content = systemText ?? "" },
        };

        if (history is not null)
        {
            foreach (var h in history)
            {
                if (string.IsNullOrWhiteSpace(h.content)) continue;
                messages.Add(new OllamaMessage { role = h.role, content = h.content });
            }
        }

        messages.Add(new OllamaMessage { role = "user", content = userText ?? "" });

        var req = new OllamaChatRequest
        {
            model = Model,
            stream = false,
            messages = messages,
        };

        using var resp = await _http.PostAsJsonAsync("/api/chat", req, cancellationToken: ct);
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadAsStringAsync(ct);
        return ExtractMessageContent(body);
    }

    public Task<string> PostForTextAsync(string systemText, string userText, CancellationToken ct)
        => PostChatAsync(systemText, userText, null, ct);

    private static string Safe(string? s)
    {
        s ??= "";
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        if (s.Length > 140) s = s[..140] + "…";
        return s;
    }

    private static bool TryParseAiReply(string content, out AiReply reply)
    {
        reply = new AiReply("", "neutral");
        if (string.IsNullOrWhiteSpace(content))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            var r = root.TryGetProperty("reply", out var replyProp) ? replyProp.GetString() ?? "" : "";
            var e = root.TryGetProperty("emotion", out var emoProp) ? emoProp.GetString() ?? "neutral" : "neutral";

            r = r.Trim();
            if (r.Length == 0)
                return false;

            reply = new AiReply(r, NormalizeEmotion(e));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeEmotion(string emotion)
    {
        emotion = (emotion ?? "").Trim();
        if (emotion.Length == 0) return "neutral";

        // Keep custom emotions the user/model might use (anime vibes),
        // but normalize common ones for consistent coloring.
        var lower = emotion.ToLowerInvariant();
        return lower switch
        {
            "happy" => "happy",
            "sad" => "sad",
            "angry" => "angry",
            "surprised" => "surprised",
            "playful" => "playful",
            "thinking" => "thinking",
            "neutral" => "neutral",
            _ => emotion.Length > 24 ? emotion[..24] : emotion,
        };
    }

    // Minimal request DTO matching Ollama /api/chat
    private sealed class OllamaChatRequest
    {
        public string model { get; set; } = "";
        public bool stream { get; set; }
        public List<OllamaMessage> messages { get; set; } = [];
        public OllamaChatOptions? options { get; set; }
    }

    private sealed class OllamaChatOptions
    {
        public int? num_predict { get; set; }
        public string? keep_alive { get; set; }
    }

    private sealed class OllamaMessage
    {
        public string role { get; set; } = "";
        public string content { get; set; } = "";
    }

    // Response DTO not required (we parse JSON string directly for robustness).

    private static string ExtractMessageContent(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "";

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.Object &&
                message.TryGetProperty("content", out var content))
            {
                return (content.GetString() ?? "").Trim();
            }
        }
        catch
        {
            // ignore and fall back to raw body below
        }

        return body.Trim();
    }

    public async Task<(string Reply, string Emotion)> StreamChatAsync(
        string systemPrompt,
        string userText,
        IReadOnlyList<(string role, string content)>? history,
        Action<string> onPartialReply,
        CancellationToken ct
    )
    {
        var messages = new List<OllamaMessage>
        {
            new() { role = "system", content = systemPrompt ?? "" },
        };

        if (history is not null)
        {
            foreach (var h in history)
            {
                if (string.IsNullOrWhiteSpace(h.content)) continue;
                messages.Add(new OllamaMessage { role = h.role, content = h.content });
            }
        }

        messages.Add(new OllamaMessage { role = "user", content = userText ?? "" });

        var req = new OllamaChatRequest
        {
            model = Model,
            stream = true,
            messages = messages,
            options = new OllamaChatOptions
            {
                num_predict = 100,
                keep_alive = "10m"
            }
        };

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(req)
        };

        using var resp = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var buffer = new StringBuilder();
        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("message", out var msg) &&
                    msg.TryGetProperty("content", out var contentProp))
                {
                    var chunk = contentProp.GetString() ?? "";
                    buffer.Append(chunk);
                    var partial = ExtractReplyFromLabeled(buffer.ToString(), partialOk: true);
                    if (!string.IsNullOrWhiteSpace(partial))
                        onPartialReply(partial);
                }
            }
            catch
            {
                // ignore malformed chunk
            }
        }

        var full = buffer.ToString();
        var (emotion, reply) = ParseLabeledReply(full);
        return (reply, emotion);
    }

    private static string ExtractReplyFromLabeled(string text, bool partialOk)
    {
        var idx = text.IndexOf("Reply:", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return "";
        var after = text[(idx + "Reply:".Length)..].TrimStart();
        if (!partialOk)
            return after.Trim();
        return after;
    }

    private static (string emotion, string reply) ParseLabeledReply(string text)
    {
        var emotion = "neutral";
        var reply = text;

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith("Emotion:", StringComparison.OrdinalIgnoreCase))
                emotion = line["Emotion:".Length..].Trim();
            if (line.StartsWith("Reply:", StringComparison.OrdinalIgnoreCase))
                reply = line["Reply:".Length..].Trim();
        }

        return (NormalizeEmotion(emotion), reply);
    }

    private static bool LooksLikeJson(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length < 2) return false;
        if (!text.StartsWith("{") || !text.EndsWith("}")) return false;
        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> IsModelAvailableAsync(string model, CancellationToken ct = default)
    {
        model = (model ?? "").Trim();
        if (model.Length == 0) return false;

        try
        {
            // Cache tags for a short window to avoid spamming /api/tags.
            if (DateTimeOffset.Now - _lastTagsAt > TimeSpan.FromSeconds(30))
            {
                using var resp = await _http.GetAsync("/api/tags", ct);
                if (!resp.IsSuccessStatusCode) return false;

                using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                if (!doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                    return false;

                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var m in models.EnumerateArray())
                {
                    if (m.TryGetProperty("name", out var nameProp))
                    {
                        var name = (nameProp.GetString() ?? "").Trim();
                        if (name.Length > 0) set.Add(name);
                    }
                }

                _lastTags = set;
                _lastTagsAt = DateTimeOffset.Now;
            }

            return _lastTags.Contains(model);
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> IsModelRunningAsync(string model, CancellationToken ct = default)
    {
        model = (model ?? "").Trim();
        if (model.Length == 0) return false;

        try
        {
            // Cache /api/ps briefly to avoid polling overload.
            if (DateTimeOffset.Now - _lastPsAt > TimeSpan.FromSeconds(2))
            {
                using var resp = await _http.GetAsync("/api/ps", ct);
                if (!resp.IsSuccessStatusCode) return false;

                using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                if (!doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                    return false;

                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var m in models.EnumerateArray())
                {
                    if (m.TryGetProperty("name", out var nameProp))
                    {
                        var name = (nameProp.GetString() ?? "").Trim();
                        if (name.Length > 0) set.Add(name);
                    }
                }

                _lastPs = set;
                _lastPsAt = DateTimeOffset.Now;
            }

            return _lastPs.Contains(model);
        }
        catch
        {
            return false;
        }
    }
}

