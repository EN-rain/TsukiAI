using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using System.IO;
using TsukiAI.Desktop;
using TsukiAI.Desktop.Models;

namespace TsukiAI.Desktop.Services;

public sealed class OllamaClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ResponseCache _cache;
    public string Model { get; private set; }
    private DateTimeOffset _lastTagsAt = DateTimeOffset.MinValue;
    private HashSet<string> _lastTags = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastPsAt = DateTimeOffset.MinValue;
    private HashSet<string> _lastPs = new(StringComparer.OrdinalIgnoreCase);
    
    // Performance settings - optimized for speed
    private const int MAX_CONTEXT_MESSAGES = 4; // Only last 4 messages (was 6)
    private const int MAX_TOKENS = 50; // Enforce short replies (~2 sentences)
    private const int CONTEXT_WINDOW = 1024; // Smaller context (was 2048)

    /// <summary>
    /// True if the model has been warmed up and is ready for fast responses.
    /// </summary>
    public bool IsWarmedUp { get; private set; } = false;

    public OllamaClient(string model = "qwen2.5:3b", string baseUrl = "http://localhost:11434")
    {
        Model = model;
        
        // Use SocketsHttpHandler for connection pooling and better performance
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 10,
            EnableMultipleHttp2Connections = true,
        };
        
        _http = new HttpClient(handler)
        { 
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(300) // 5 minutes for first load
        };
        
        _cache = new ResponseCache(maxEntries: 100, ttl: TimeSpan.FromMinutes(10));
        
        // Pre-warm common responses
        _cache.PreWarmCommonResponses("Tsuki");
    }

    /// <summary>
    /// Checks if Ollama server is reachable.
    /// </summary>
    public async Task<bool> IsServerReachableAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var response = await _http.GetAsync("/api/tags", cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public void SetModel(string model)
    {
        model = (model ?? "").Trim();
        if (model.Length == 0) return;
        Model = model;
        _cache.Clear(); // Clear cache when model changes
    }

    /// <summary>
    /// Warms up the model with a minimal request to reduce first-token latency.
    /// </summary>
    public async Task<bool> WarmupModelAsync(string? model = null, CancellationToken ct = default)
    {
        model = string.IsNullOrWhiteSpace(model) ? Model : model.Trim();
        try
        {
            var req = new
            {
                model,
                messages = new[]
                {
                    new { role = "system", content = "Warmup." },
                    new { role = "user", content = "ping" }
                },
                stream = false,
                options = new
                {
                    num_predict = 5,
                    temperature = 0.1
                },
                keep_alive = 86400
            };

            using var resp = await _http.PostAsJsonAsync("/api/chat", req, ct);
            var ok = resp.IsSuccessStatusCode;
            if (ok) 
            {
                DevLog.WriteLine("Ollama warmup OK: {0}", model ?? "(null)");
                IsWarmedUp = true;
            }
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

    /// <summary>
    /// STREAMING chat with emotion. Returns final reply but streams partial responses.
    /// </summary>
    public async Task<AiReply> ChatWithEmotionStreamingAsync(
        string userText,
        string? personaName = null,
        string? preferredEmotion = null,
        IReadOnlyList<(string role, string content)>? history = null,
        Action<string>? onPartialReply = null,
        CancellationToken ct = default
    )
    {
        // Check cache first (only for non-streaming fallback)
        var contextHash = ComputeContextHash(history);
        var cached = _cache.Get(contextHash, userText);
        if (cached != null && onPartialReply == null)
        {
            DevLog.WriteLine("OllamaClient: Cache hit for input");
            return new AiReply(cached.Reply, cached.Emotion);
        }

        personaName = string.IsNullOrWhiteSpace(personaName) ? "Tsuki" : personaName.Trim();
        preferredEmotion = (preferredEmotion ?? "").Trim();

        var personaLine = $"Your name is {personaName}.";
        var emotionLine = preferredEmotion.Length == 0
            ? ""
            : $"Your baseline emotion is \"{preferredEmotion}\" unless the user requests a different mood.";

        const string jsonSchema = """{"reply":"string","emotion":"one of: happy,sad,angry,surprised,playful,thinking,neutral"}""";
        var system =
            $"""
You are {personaName}, a lively, expressive local AI companion.
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

        // Trim history to last N messages for speed
        var trimmedHistory = TrimHistory(history);

        // Stream the response
        var (reply, emotion) = await StreamChatAsync(
            system, 
            userText ?? "", 
            trimmedHistory, 
            onPartialReply ?? (_ => { }), 
            ct
        );

        // Cache the final result
        if (!string.IsNullOrWhiteSpace(reply))
        {
            _cache.Set(contextHash, userText, reply, emotion);
        }

        return new AiReply(reply, emotion);
    }

    /// <summary>
    /// Non-streaming fallback for simple queries.
    /// </summary>
    public async Task<AiReply> ChatWithEmotionAsync(
        string userText,
        string? personaName = null,
        string? preferredEmotion = null,
        IReadOnlyList<(string role, string content)>? history = null,
        CancellationToken ct = default
    )
    {
        // Check cache first
        var contextHash = ComputeContextHash(history);
        var cached = _cache.Get(contextHash, userText);
        if (cached != null)
        {
            DevLog.WriteLine("OllamaClient: Cache hit");
            return new AiReply(cached.Reply, cached.Emotion);
        }

        personaName = string.IsNullOrWhiteSpace(personaName) ? "Tsuki" : personaName.Trim();
        preferredEmotion = (preferredEmotion ?? "").Trim();

        var personaLine = $"Your name is {personaName}.";
        var emotionLine = preferredEmotion.Length == 0
            ? ""
            : $"Your baseline emotion is \"{preferredEmotion}\" unless the user requests a different mood.";

        const string jsonSchema = """{"reply":"string","emotion":"one of: happy,sad,angry,surprised,playful,thinking,neutral"}""";
        var system =
            $"""
You are {personaName}, a lively, expressive local AI companion.
You are playful, warm, casually confident, and you like light teasing.
Use short to medium sentences. Sound natural, not formal.
Avoid repeating your last response.

Return ONLY valid JSON, no markdown, no extra text.
Schema:
{jsonSchema}
Be concise in reply (1-2 sentences).
"""
            + "\n"
            + personaLine
            + (emotionLine.Length == 0 ? "" : "\n" + emotionLine);

        // Trim history for speed
        var trimmedHistory = TrimHistory(history);

        var content = await PostChatAsync(system, userText ?? "", trimmedHistory, ct);
        if (string.IsNullOrWhiteSpace(content))
        {
            await Task.Delay(150, ct);
            content = await PostChatAsync(system, userText ?? "", trimmedHistory, ct);
        }

        if (TryParseAiReply(content, out var parsed))
        {
            _cache.Set(contextHash, userText, parsed.Reply, parsed.Emotion);
            return parsed;
        }

        // Fallback: treat as plain text
        if (LooksLikeJson(content))
        {
            return new AiReply("Hmm, give me a sec—try again?", "neutral");
        }

        var fallbackReply = string.IsNullOrWhiteSpace(content) ? "Hmm, I didn't catch that—try again?" : content;
        _cache.Set(contextHash, userText, fallbackReply, "neutral");
        return new AiReply(fallbackReply, "neutral");
    }

    /// <summary>
    /// Trims history to last N messages to reduce token processing.
    /// </summary>
    private static List<(string role, string content)>? TrimHistory(IReadOnlyList<(string role, string content)>? history)
    {
        if (history == null || history.Count <= MAX_CONTEXT_MESSAGES)
            return history?.ToList();

        // Keep the most recent N messages
        return history.Skip(history.Count - MAX_CONTEXT_MESSAGES).ToList();
    }

    private static string ComputeContextHash(IReadOnlyList<(string role, string content)>? history)
    {
        if (history == null || history.Count == 0)
            return "";

        // Simple hash of recent message contents
        var recent = string.Join("|", history.TakeLast(3).Select(h => h.content[..Math.Min(20, h.content.Length)]));
        var bytes = System.Text.Encoding.UTF8.GetBytes(recent);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16];
    }

    #region Streaming Implementation

    /// <summary>
    /// Streams chat response token by token for immediate UI feedback.
    /// </summary>
    public async Task<(string Reply, string Emotion)> StreamChatAsync(
        string systemPrompt,
        string userText,
        List<(string role, string content)>? history,
        Action<string> onPartialReply,
        CancellationToken ct
    )
    {
        var totalSw = Stopwatch.StartNew();
        DevLog.WriteLine("StreamChat: Starting request...");
        
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
            options = new OllamaOptions
            {
                num_predict = MAX_TOKENS,
                num_ctx = CONTEXT_WINDOW,
                temperature = 0.7f,
                top_p = 0.9f,
                top_k = 40
            }
        };

        var reqSw = Stopwatch.StartNew();
        using var httpReq = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(req)
        };

        // Send request with response headers read to start getting data ASAP
        DevLog.WriteLine("StreamChat: Sending HTTP request...");
        using var resp = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct);
        reqSw.Stop();
        DevLog.WriteLine("StreamChat: HTTP headers received in {0}ms", reqSw.ElapsedMilliseconds);
        
        resp.EnsureSuccessStatusCode();

        var streamSw = Stopwatch.StartNew();
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8, bufferSize: 1024);
        streamSw.Stop();
        DevLog.WriteLine("StreamChat: Stream opened in {0}ms", streamSw.ElapsedMilliseconds);

        var buffer = new StringBuilder();
        var partialEmitted = false;
        var lastEmitTime = DateTimeOffset.Now;
        var firstTokenReceived = false;
        var tokenSw = Stopwatch.StartNew();

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                
                // Check for done signal
                if (doc.RootElement.TryGetProperty("done", out var doneProp) && doneProp.GetBoolean())
                    break;

                if (doc.RootElement.TryGetProperty("message", out var msg) &&
                    msg.TryGetProperty("content", out var contentProp))
                {
                    var chunk = contentProp.GetString() ?? "";
                    buffer.Append(chunk);
                    
                    // Log first token timing
                    if (!firstTokenReceived && !string.IsNullOrWhiteSpace(chunk))
                    {
                        firstTokenReceived = true;
                        DevLog.WriteLine("StreamChat: First token received after {0}ms", tokenSw.ElapsedMilliseconds);
                    }
                    
                    // Emit partial reply every 100ms or on significant chunks
                    var now = DateTimeOffset.Now;
                    if ((now - lastEmitTime).TotalMilliseconds > 100 || chunk.Contains(' ') || chunk.Contains('\n'))
                    {
                        var partial = TryExtractPartialReply(buffer.ToString());
                        if (!string.IsNullOrWhiteSpace(partial))
                        {
                            onPartialReply(partial);
                            partialEmitted = true;
                            lastEmitTime = now;
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Ignore malformed chunks and continue
            }
        }

        totalSw.Stop();
        DevLog.WriteLine("StreamChat: Total time {0}ms (first token: {1}ms)", 
            totalSw.ElapsedMilliseconds, 
            firstTokenReceived ? tokenSw.ElapsedMilliseconds.ToString() : "N/A");

        var full = buffer.ToString().Trim();
        
        // Parse final JSON response
        if (TryParseAiReply(full, out var parsed))
        {
            if (!partialEmitted)
                onPartialReply(parsed.Reply);
            return (parsed.Reply, parsed.Emotion);
        }

        // Fallback: treat as plain text
        if (!partialEmitted)
            onPartialReply(full);
        return (full, "neutral");
    }

    /// <summary>
    /// Attempts to extract a partial reply from incomplete JSON for streaming.
    /// </summary>
    private static string? TryExtractPartialReply(string text)
    {
        // Look for "reply":" and extract content up to the closing quote
        var startIdx = text.IndexOf("\"reply\":\"", StringComparison.OrdinalIgnoreCase);
        if (startIdx < 0) return null;

        startIdx += "\"reply\":\"".Length;
        
        // Find the end - look for unescaped closing quote
        var endIdx = startIdx;
        while (endIdx < text.Length)
        {
            if (text[endIdx] == '"' && (endIdx == 0 || text[endIdx - 1] != '\\'))
                break;
            endIdx++;
        }

        if (endIdx > startIdx)
        {
            return text[startIdx..endIdx].Replace("\\n", "\n").Replace("\\\"", "\"");
        }

        // If no closing quote yet, return what we have
        return text[startIdx..].Replace("\\n", "\n").Replace("\\\"", "\"");
    }

    #endregion

    #region Activity Summarization

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

        // Ensure declarative output (no question marks)
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

    #endregion

    #region Helper Methods

    /// <summary>
    /// Simple text-to-text request without history.
    /// </summary>
    public async Task<string> PostForTextAsync(string systemText, string userText, CancellationToken ct)
    {
        var messages = new List<OllamaMessage>
        {
            new() { role = "system", content = systemText ?? "" },
            new() { role = "user", content = userText ?? "" }
        };

        var req = new OllamaChatRequest
        {
            model = Model,
            stream = false,
            messages = messages,
            options = new OllamaOptions
            {
                num_predict = MAX_TOKENS,
                num_ctx = CONTEXT_WINDOW,
                temperature = 0.7f,
                top_p = 0.9f,
                top_k = 40
            }
        };

        using var resp = await _http.PostAsJsonAsync("/api/chat", req, cancellationToken: ct);
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadAsStringAsync(ct);
        return ExtractMessageContent(body);
    }

    private async Task<string> PostChatAsync(
        string systemText,
        string userText,
        List<(string role, string content)>? history,
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
            options = new OllamaOptions
            {
                num_predict = MAX_TOKENS,
                num_ctx = CONTEXT_WINDOW,
                temperature = 0.7f,
                top_p = 0.9f,
                top_k = 40
            }
        };

        using var resp = await _http.PostAsJsonAsync("/api/chat", req, cancellationToken: ct);
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadAsStringAsync(ct);
        return ExtractMessageContent(body);
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

    private static string Safe(string? s)
    {
        s ??= "";
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        if (s.Length > 140) s = s[..140] + "…";
        return s;
    }

    public async Task<bool> IsModelAvailableAsync(string model, CancellationToken ct = default)
    {
        model = (model ?? "").Trim();
        if (model.Length == 0) return false;

        try
        {
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

    public void Dispose()
    {
        _http?.Dispose();
    }

    #endregion

    #region DTOs

    private sealed class OllamaChatRequest
    {
        public string model { get; set; } = "";
        public bool stream { get; set; }
        public List<OllamaMessage> messages { get; set; } = [];
        public OllamaOptions? options { get; set; }
    }

    private sealed class OllamaMessage
    {
        public string role { get; set; } = "";
        public string content { get; set; } = "";
    }

    private sealed class OllamaOptions
    {
        public int num_predict { get; set; }
        public int num_ctx { get; set; }
        public float temperature { get; set; }
        public float top_p { get; set; }
        public int top_k { get; set; }
    }

    #endregion
}
