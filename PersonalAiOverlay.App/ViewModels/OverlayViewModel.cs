using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Collections.ObjectModel;
using System.Linq;
using System.Collections.Generic;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using PersonalAiOverlay.App.Services;
using PersonalAiOverlay.Core.Assistant;

namespace PersonalAiOverlay.App.ViewModels;

public sealed class OverlayViewModel : INotifyPropertyChanged
{
    private readonly AssistantEngine _engine;
    private readonly OllamaClient _ollama;
    private readonly MemoryStore _memoryStore;
    private readonly MemoryRules _memoryRules;
    private readonly PromptBuilder _promptBuilder;
    private readonly PersonalityBiasService? _personalityBias;
    private CancellationTokenSource? _chatCts;
    private string _inputText = "";
    private string _conversationText = "";
    private string _displayedConversationText = "";
    private DispatcherTimer? _typewriterTimer;
    private readonly StringBuilder _conversationBuilder = new();
    private string _fiveMinuteSummariesText = "";
    private readonly StringBuilder _fiveMinuteSummariesBuilder = new();
    private string _hourlySummariesText = "";
    private readonly StringBuilder _hourlySummariesBuilder = new();
    private string _statusText = "";
    private string _emotion = "neutral";
    private Brush _accentBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x7F, 0xFF));
    private string _assistantName = "Tsuki";
    private string _preferredEmotion = "neutral";
    private string _modelName = "llama3.2:3b";
    private readonly Queue<(string role, string content)> _context = new();
    private readonly Queue<string> _recentReplies = new();
    private const int MaxContextMessages = 4;
    private const int MaxRecentReplies = 3;
    private const int CompressionThreshold = 12;
    private const int KeepAfterCompression = 4;

    private string _ollamaStatus = "Ollama: unknown";
    private string _modelStatus = "Model: unknown";
    private double _cpuPercent;
    private double _memoryMb;

    private bool _activityLoggingEnabled;
    private bool _isThinking;
    private int _tickIndex;
    private DateTimeOffset? _lastCaptureAt;
    private string _nextCaptureInText = "Next: --";
    private DateTimeOffset _lastUserMessageAt = DateTimeOffset.Now;
    private DispatcherTimer? _proactiveTimer;
    private TimeSpan _proactiveAfter = TimeSpan.FromMinutes(10);
    private bool _proactiveEnabled;
    private bool _startupGreetingSent;

    public OverlayViewModel(
        AssistantEngine engine,
        OllamaClient? ollama,
        MemoryStore memoryStore,
        MemoryRules memoryRules,
        PromptBuilder promptBuilder,
        PersonalityBiasService? personalityBias = null
    )
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _ollama = ollama ?? new OllamaClient();
        _memoryStore = memoryStore;
        _memoryRules = memoryRules;
        _promptBuilder = promptBuilder;
        _personalityBias = personalityBias;
        SubmitCommand = new AsyncRelayCommand(SubmitAsync);
        CopyConversationCommand = new RelayCommand(CopyConversation);
        ClearCommand = new RelayCommand(Clear);
        // 12 dots = 12 * 5min = 60min
        TickDots = new ObservableCollection<bool>(Enumerable.Repeat(false, 12));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when Tsuki posts a reply (for tray notification when window not focused).</summary>
    public event Action<string>? AssistantReplied;

    public ICommand SubmitCommand { get; }
    public ICommand CopyConversationCommand { get; }
    public ICommand ClearCommand { get; }

    public ObservableCollection<bool> TickDots { get; }

    public string FiveMinuteSummariesText
    {
        get => _fiveMinuteSummariesText;
        private set
        {
            if (value == _fiveMinuteSummariesText) return;
            _fiveMinuteSummariesText = value;
            OnPropertyChanged();
        }
    }

    public string HourlySummariesText
    {
        get => _hourlySummariesText;
        private set
        {
            if (value == _hourlySummariesText) return;
            _hourlySummariesText = value;
            OnPropertyChanged();
        }
    }

    public string OllamaStatus
    {
        get => _ollamaStatus;
        set
        {
            if (value == _ollamaStatus) return;
            _ollamaStatus = value;
            OnPropertyChanged();
        }
    }

    public string ModelStatus
    {
        get => _modelStatus;
        set
        {
            if (value == _modelStatus) return;
            _modelStatus = value;
            OnPropertyChanged();
        }
    }

    public double CpuPercent
    {
        get => _cpuPercent;
        set
        {
            if (Math.Abs(value - _cpuPercent) < 0.01) return;
            _cpuPercent = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CpuText));
        }
    }

    public double MemoryMb
    {
        get => _memoryMb;
        set
        {
            if (Math.Abs(value - _memoryMb) < 0.01) return;
            _memoryMb = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MemoryText));
        }
    }

    public string CpuText => $"CPU {CpuPercent:0}%";
    public string MemoryText => $"RAM {MemoryMb:0} MB";

    public string NextCaptureInText
    {
        get => _nextCaptureInText;
        private set
        {
            if (value == _nextCaptureInText) return;
            _nextCaptureInText = value;
            OnPropertyChanged();
        }
    }

    public bool ActivityLoggingEnabled
    {
        get => _activityLoggingEnabled;
        set
        {
            if (value == _activityLoggingEnabled) return;
            _activityLoggingEnabled = value;
            OnPropertyChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (value == _statusText) return;
            _statusText = value;
            OnPropertyChanged();
        }
    }

    public string AssistantName
    {
        get => _assistantName;
        set
        {
            value = string.IsNullOrWhiteSpace(value) ? "Airi" : value.Trim();
            if (value == _assistantName) return;
            _assistantName = value;
            OnPropertyChanged();
        }
    }

    // User-configured baseline emotion (free text). We pass this into Ollama so it can follow it.
    public string PreferredEmotion
    {
        get => _preferredEmotion;
        set
        {
            value = string.IsNullOrWhiteSpace(value) ? "neutral" : value.Trim();
            if (value == _preferredEmotion) return;
            _preferredEmotion = value;
            OnPropertyChanged();
        }
    }

    public string Emotion
    {
        get => _emotion;
        private set
        {
            value = string.IsNullOrWhiteSpace(value) ? "neutral" : value;
            if (value == _emotion) return;
            _emotion = value;
            AccentBrush = BuildAccentBrush(_emotion);
            OnPropertyChanged();
            OnPropertyChanged(nameof(EmotionKaomoji));
        }
    }

    public void ApplySettings(string assistantName, string preferredEmotion, string? modelName = null)
    {
        AssistantName = assistantName;
        PreferredEmotion = preferredEmotion;
        ModelName = string.IsNullOrWhiteSpace(modelName) ? _modelName : modelName.Trim();
        Emotion = PreferredEmotion; // reflect immediately in UI
    }

    public string ModelName
    {
        get => _modelName;
        set
        {
            value = string.IsNullOrWhiteSpace(value) ? "llama3.2:3b" : value.Trim();
            if (value == _modelName) return;
            _modelName = value;
            OnPropertyChanged();
        }
    }

    public string EmotionKaomoji => Emotion switch
    {
        "happy" => "(＾▽＾)",
        "sad" => "(；＿；)",
        "angry" => "(＃＞＜)",
        "surprised" => "(O_O)",
        "playful" => "(≧▽≦)",
        "thinking" => "(・_・?)",
        "idle" => "(－ω－)",
        "focused" => "(´ー`)",
        "frustrated" => "(＃＞＜)",
        "sleepy" => "(´～`)",
        "bored" => "( ́︿ ̀)",
        "concerned" => "(´・ω・`)",
        _ => "(・ω・)",
    };

    public Brush AccentBrush
    {
        get => _accentBrush;
        private set
        {
            if (Equals(value, _accentBrush)) return;
            _accentBrush = value;
            OnPropertyChanged();
        }
    }

    public string InputText
    {
        get => _inputText;
        set
        {
            if (value == _inputText) return;
            _inputText = value;
            OnPropertyChanged();
        }
    }

    public string ConversationText
    {
        get => _conversationText;
        private set
        {
            if (System.Windows.Application.Current?.Dispatcher.CheckAccess() == false)
            {
                var v = value;
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() => SetConversationTextCore(v));
                return;
            }
            SetConversationTextCore(value);
        }
    }

    /// <summary>Conversation text revealed character-by-character (typewriter effect).</summary>
    public string DisplayedConversationText
    {
        get => _displayedConversationText;
        private set
        {
            if (value == _displayedConversationText) return;
            _displayedConversationText = value ?? "";
            OnPropertyChanged();
        }
    }

    public bool IsThinking
    {
        get => _isThinking;
        private set
        {
            if (value == _isThinking) return;
            _isThinking = value;
            OnPropertyChanged();
        }
    }

    private async Task SubmitAsync()
    {
        var text = (InputText ?? "").Trim();
        if (text.Length == 0) return;

        _lastUserMessageAt = DateTimeOffset.Now;
        AppendLineFast($"{AppConstants.OwnerName}: {text}");
        InputText = "";

        var historySnapshot = _context.ToList();

        // First try built-in commands. If not handled, fall back to Ollama (dolphin-llama3).
        if (_engine.TryHandle(text, out var commandResponse))
        {
            Emotion = "neutral";
            var reply = EnsureNonRepeating(commandResponse.AssistantText);
            AppendAssistant(reply);
            AddToContext("user", text);
            AddToContext("assistant", reply);
            return;
        }

        // Memory: explicit or auto-low-risk.
        if (_memoryRules.TryExtract(text, out var mem) && mem is not null)
            _memoryStore.AddOrUpdate(mem);

        try
        {
            Emotion = "thinking";
            IsThinking = true;
            StatusText = $"Thinking ({_ollama.Model})...";

            var memories = _memoryStore.GetRelevant(text, max: 5);
            var timeOfDay = TimeOfDayService.GetTimeOfDayTag();
            var personalityHint = _personalityBias?.GetPromptHint();
            var systemPrompt = _promptBuilder.BuildChatSystemPrompt(Emotion, memories, timeOfDay, personalityHint);

            // Cancel any existing stream.
            _chatCts?.Cancel();
            _chatCts = new CancellationTokenSource();

            StartAssistantStream();
            var ai = await _ollama.StreamChatAsync(
                systemPrompt,
                text,
                historySnapshot,
                partial => UpdateAssistantStream(partial),
                _chatCts.Token
            );

            Emotion = ai.Emotion;
            var finalReply = string.IsNullOrWhiteSpace(ai.Reply) ? "(no response from model)" : ai.Reply.Trim();
            finalReply = EnsureNonRepeating(finalReply);
            EndAssistantStream(finalReply);

            AddToContext("user", text);
            AddToContext("assistant", finalReply);
            TryCompressConversation();
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("Chat error: {0}", ex.Message);
            Emotion = "sad";
            var reply = $"Ollama error: {ex.Message}\nMake sure Ollama is running and the model is pulled (e.g. `ollama pull llama3.2:3b`).";
            reply = EnsureNonRepeating(reply);
            AppendAssistant(reply);

            AddToContext("user", text);
            AddToContext("assistant", reply);
        }
        finally
        {
            IsThinking = false;
            StatusText = "";
        }
    }

    private void AppendAssistant(string assistantText)
    {
        AppendLineFast($"Tsuki: {assistantText}");
        AssistantReplied?.Invoke(assistantText);
    }

    /// <summary>Allows the app to post a message without user input (startup greeting, proactive nudges).</summary>
    public void PostAssistantMessage(string assistantText, bool addToContext = true)
    {
        assistantText = (assistantText ?? "").Trim();
        if (assistantText.Length == 0) return;

        void Do()
        {
            AppendAssistant(EnsureNonRepeating(assistantText));
            if (addToContext)
                AddToContext("assistant", assistantText);
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(Do);
        else
            Do();
    }

    public void SendStartupGreetingIfNeeded()
    {
        if (_startupGreetingSent) return;
        if (_conversationBuilder.Length > 0) return; // don't spam if user already chatted

        _startupGreetingSent = true;
        _lastUserMessageAt = DateTimeOffset.Now; // start the idle countdown from launch
        PostAssistantMessage($"Hey {AppConstants.OwnerName}. I’m loaded and ready—what are we working on?", addToContext: false);
    }

    public void ConfigureProactiveMessages(bool enabled, int afterMinutes)
    {
        afterMinutes = Math.Clamp(afterMinutes, 1, 1440);

        _proactiveEnabled = enabled;
        _proactiveAfter = TimeSpan.FromMinutes(afterMinutes);

        if (!enabled)
        {
            _proactiveTimer?.Stop();
            return;
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        _proactiveTimer ??= new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(10)
        };
        _proactiveTimer.Tick -= ProactiveTimerOnTick;
        _proactiveTimer.Tick += ProactiveTimerOnTick;
        if (!_proactiveTimer.IsEnabled)
            _proactiveTimer.Start();
    }

    private void ProactiveTimerOnTick(object? sender, EventArgs e)
    {
        if (!_proactiveEnabled) return;
        if (IsThinking || _streaming) return;

        var idleFor = DateTimeOffset.Now - _lastUserMessageAt;
        if (idleFor < _proactiveAfter) return;

        _lastUserMessageAt = DateTimeOffset.Now; // reset so we don't spam
        PostAssistantMessage(BuildProactiveNudge(), addToContext: false);
    }

    private string BuildProactiveNudge()
    {
        var timeOfDay = TimeOfDayService.GetTimeOfDayTag();
        var emotion = (Emotion ?? "").Trim().ToLowerInvariant();

        // Keep these short + non-creepy. (Use plain arrays for widest C# compatibility.)
        string[] lines;
        if (timeOfDay == "late_night")
        {
            lines =
            [
                $"Psst… it’s late. If you’re stuck, tell me what part is annoying you, {AppConstants.OwnerName}.",
                "Late-night grind again… okay. Just don’t forget to breathe and drink water.",
            ];
        }
        else if (emotion == "focused")
        {
            lines =
            [
                "Still in the zone? Nice. Want a quick plan/checklist for the next step?",
                "You’re focused—keep that momentum. If something’s blocking you, drop it here.",
            ];
        }
        else if (emotion == "frustrated")
        {
            lines =
            [
                "Hey. If something broke, paste the error and I’ll help you untangle it.",
                "Okay, pause. What’s the one thing that isn’t working right now?",
            ];
        }
        else if (emotion == "bored")
        {
            lines =
            [
                "You went quiet. Want me to suggest a tiny next step so you can get moving again?",
                "If you’re procrastinating… I get it. Want a 5‑minute task to warm up?",
            ];
        }
        else
        {
            lines =
            [
                $"Hey {AppConstants.OwnerName}—I’m here. Want to continue?",
                "Checking in. Need help with anything?",
            ];
        }

        return lines[Random.Shared.Next(lines.Length)];
    }

    private string _streamBase = "";
    private bool _streaming;
    private DateTimeOffset _lastStreamUiUpdate;
    private const int StreamUiThrottleMs = 50;

    private void StartAssistantStream()
    {
        _streaming = true;
        _lastStreamUiUpdate = DateTimeOffset.MinValue;
        _streamBase = _conversationBuilder.ToString();
        var prefix = _streamBase.Length == 0 ? "" : "\n";
        ConversationText = _streamBase + prefix + "Tsuki: ";
    }

    private void UpdateAssistantStream(string partial)
    {
        if (!_streaming) return;
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastStreamUiUpdate).TotalMilliseconds < StreamUiThrottleMs)
            return;
        _lastStreamUiUpdate = now;
        var prefix = _streamBase.Length == 0 ? "" : "\n";
        ConversationText = _streamBase + prefix + "Tsuki: " + partial;
    }

    private void SetConversationTextCore(string value)
    {
        if (value == _conversationText) return;
        _conversationText = value ?? "";
        OnPropertyChanged(nameof(ConversationText));
        // If target got shorter (e.g. new stream), snap displayed to avoid showing stale text
        if (_displayedConversationText.Length > _conversationText.Length)
        {
            _displayedConversationText = _conversationText;
            OnPropertyChanged(nameof(DisplayedConversationText));
        }
        // While streaming, show text immediately so reply feels fast; typewriter only for non-streaming
        if (_streaming)
        {
            _displayedConversationText = _conversationText;
            OnPropertyChanged(nameof(DisplayedConversationText));
            return;
        }
        StartTypewriter();
    }

    private void StartTypewriter()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) return;

        if (_typewriterTimer == null)
        {
            _typewriterTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(28)
            };
            _typewriterTimer.Tick += TypewriterTick;
        }

        if (_displayedConversationText.Length >= _conversationText.Length)
        {
            _typewriterTimer.Stop();
            return;
        }
        if (!_typewriterTimer.IsEnabled)
            _typewriterTimer.Start();
    }

    private void TypewriterTick(object? sender, EventArgs e)
    {
        if (_displayedConversationText.Length >= _conversationText.Length)
        {
            _typewriterTimer?.Stop();
            return;
        }
        var toAdd = Math.Min(2, _conversationText.Length - _displayedConversationText.Length);
        _displayedConversationText = _conversationText.Substring(0, _displayedConversationText.Length + toAdd);
        OnPropertyChanged(nameof(DisplayedConversationText));
    }

    private void EndAssistantStream(string finalText)
    {
        _streaming = false;
        AppendAssistant(finalText);
        _streamBase = "";
        // Show full reply immediately so stream end is not slowed by typewriter
        _displayedConversationText = _conversationText;
        OnPropertyChanged(nameof(DisplayedConversationText));
    }

    private void AppendLineFast(string line)
    {
        if (_conversationBuilder.Length > 0) _conversationBuilder.AppendLine();
        _conversationBuilder.Append(line);
        ConversationText = _conversationBuilder.ToString();
    }

    private void CopyConversation()
    {
        if (!string.IsNullOrWhiteSpace(ConversationText))
            System.Windows.Clipboard.SetText(ConversationText);
    }

    private void Clear()
    {
        _typewriterTimer?.Stop();
        _conversationBuilder.Clear();
        _conversationText = "";
        _displayedConversationText = "";
        OnPropertyChanged(nameof(ConversationText));
        OnPropertyChanged(nameof(DisplayedConversationText));
        InputText = "";
        Emotion = "neutral";
        _context.Clear();
        _recentReplies.Clear();
        _streaming = false;
        _streamBase = "";
    }

    public void OnSampleTick()
    {
        if (TickDots.Count != 12) return;
        TickDots[_tickIndex] = true;
        _tickIndex = (_tickIndex + 1) % 12;
    }

    public void ResetTicks()
    {
        for (var i = 0; i < TickDots.Count; i++)
            TickDots[i] = false;
        _tickIndex = 0;
    }

    public void SetLastCaptureAt(DateTimeOffset ts)
    {
        _lastCaptureAt = ts;
    }

    public void UpdateNextCaptureCountdown(TimeSpan interval)
    {
        if (!ActivityLoggingEnabled || _lastCaptureAt is null)
        {
            NextCaptureInText = "Next: --";
            return;
        }

        var elapsed = DateTimeOffset.Now - _lastCaptureAt.Value;
        var remaining = interval - elapsed;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

        NextCaptureInText = $"Next: {remaining:mm\\:ss}";
    }

    public void AppendFiveMinuteSummary(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return;
        if (_fiveMinuteSummariesBuilder.Length > 0) _fiveMinuteSummariesBuilder.AppendLine();
        _fiveMinuteSummariesBuilder.AppendLine(text);
        FiveMinuteSummariesText = _fiveMinuteSummariesBuilder.ToString().TrimEnd();
    }

    public void AppendHourlySummary(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return;
        if (_hourlySummariesBuilder.Length > 0) _hourlySummariesBuilder.AppendLine().AppendLine();
        _hourlySummariesBuilder.AppendLine(text);
        HourlySummariesText = _hourlySummariesBuilder.ToString().TrimEnd();
    }

    private void AddToContext(string role, string content)
    {
        content = (content ?? "").Trim();
        if (content.Length == 0) return;

        _context.Enqueue((role, content));
        while (_context.Count > MaxContextMessages)
            _context.Dequeue();
    }

    private void TryCompressConversation()
    {
        if (_context.Count <= CompressionThreshold) return;
        var copy = _context.ToList();
        var toSummarize = copy.TakeLast(6).ToList();
        _ = Task.Run(async () =>
        {
            try
            {
                var summary = await _ollama.SummarizeConversationAsync(toSummarize, CancellationToken.None);
                if (!string.IsNullOrWhiteSpace(summary))
                    _memoryStore.AddLearningSummary(summary, "compression");
            }
            catch { }
        });
        var toRemove = _context.Count - KeepAfterCompression;
        for (var i = 0; i < toRemove && _context.Count > KeepAfterCompression; i++)
            _context.Dequeue();
    }

    private string EnsureNonRepeating(string reply)
    {
        reply = (reply ?? "").Trim();
        if (reply.Length == 0) return "…";

        var norm = reply.ToLowerInvariant();
        if (_recentReplies.Contains(norm))
        {
            reply = "Let me put that differently—" + reply;
        }

        _recentReplies.Enqueue(norm);
        while (_recentReplies.Count > MaxRecentReplies)
            _recentReplies.Dequeue();

        return reply;
    }


    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static Brush BuildAccentBrush(string emotion)
    {
        // Anime-ish neon accents
        var key = (emotion ?? "").Trim().ToLowerInvariant();
        var color = key switch
        {
            "happy" => Color.FromRgb(0xFF, 0x5A, 0xC8),     // pink
            "sad" => Color.FromRgb(0x4F, 0xA3, 0xFF),       // blue
            "angry" => Color.FromRgb(0xFF, 0x4F, 0x4F),     // red
            "surprised" => Color.FromRgb(0xFF, 0xD4, 0x4F), // gold
            "playful" => Color.FromRgb(0xB1, 0x4F, 0xFF),   // purple
            "thinking" => Color.FromRgb(0x2A, 0x7F, 0xFF),  // azure
            "idle" => Color.FromRgb(0x88, 0x88, 0xAA),     // gray
            "focused" => Color.FromRgb(0x2A, 0xCF, 0x7A),   // green
            "frustrated" => Color.FromRgb(0xFF, 0x6B, 0x4F),// orange-red
            "sleepy" => Color.FromRgb(0x6B, 0x7A, 0xE6),   // soft blue
            "bored" => Color.FromRgb(0x99, 0x99, 0xBB),     // muted
            "concerned" => Color.FromRgb(0xE6, 0xA8, 0x6B), // warm
            _ => Color.FromRgb(0x7A, 0xE6, 0xFF),           // cyan
        };

        var b = new SolidColorBrush(color);
        if (b.CanFreeze) b.Freeze();
        return b;
    }
}

