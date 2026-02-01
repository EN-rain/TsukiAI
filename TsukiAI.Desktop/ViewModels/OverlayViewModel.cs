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
using TsukiAI.Desktop.Services;
using TsukiAI.Desktop.Services.Ollama;
using TsukiAI.Desktop.Services.Collectors;
using TsukiAI.Core.Assistant;

namespace TsukiAI.Desktop.ViewModels;

public sealed class OverlayViewModel : INotifyPropertyChanged
{
    private readonly AssistantEngine _engine;
    private readonly OllamaClient _ollama;
    private readonly OllamaProcessManager? _processManager;
    private readonly MemoryStore _memoryStore;
    private readonly MemoryRules _memoryRules;
    private readonly PromptBuilder _promptBuilder;
    private readonly PersonalityBiasService? _personalityBias;
    private readonly ForegroundWindowCollector? _windowCollector;
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
    private string _modelName = "qwen2.5:3b";
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

    // Loading screen properties
    private bool _isLoadingModel = false;
    private string _loadingStatusText = "Initializing...";
    private string _loadingSubText = "Please wait while the AI loads";

    public OverlayViewModel(
        AssistantEngine engine,
        OllamaClient? ollama,
        MemoryStore memoryStore,
        MemoryRules memoryRules,
        PromptBuilder promptBuilder,
        PersonalityBiasService? personalityBias = null,
        OllamaProcessManager? processManager = null,
        ForegroundWindowCollector? windowCollector = null
    )
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _ollama = ollama ?? new OllamaClient();
        _processManager = processManager;
        _windowCollector = windowCollector;
        _memoryStore = memoryStore;
        _memoryRules = memoryRules;
        _promptBuilder = promptBuilder;
        _personalityBias = personalityBias;
        SubmitCommand = new AsyncRelayCommand(SubmitAsync);
        CopyConversationCommand = new RelayCommand(CopyConversation);
        ClearCommand = new RelayCommand(Clear);
        // 12 dots = 12 * 5min = 60min
        TickDots = new ObservableCollection<bool>(Enumerable.Repeat(false, 12));
        
        // Start background warmup
        _ = Task.Run(async () => await WarmupAsync());
    }
    
    private async Task WarmupAsync()
    {
        // Longer timeout for first load (model download + load can take several minutes)
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        
        try
        {
            // Show loading screen
            IsLoadingModel = true;
            LoadingStatusText = "Starting Ollama...";
            LoadingSubText = "Checking if Ollama is running";
            
            DevLog.WriteLine("OverlayViewModel: Starting warmup sequence...");
            
            // Use process manager if available
            if (_processManager != null)
            {
                DevLog.WriteLine("OverlayViewModel: Ensuring Ollama is running...");
                var ready = await _processManager.EnsureRunningAsync(cts.Token);
                if (!ready)
                {
                    DevLog.WriteLine("OverlayViewModel: Failed to ensure Ollama running");
                    LoadingStatusText = "Ollama not responding";
                    LoadingSubText = "⚠️ Start Ollama manually:\n1. Open Command Prompt\n2. Run: ollama serve\n3. Then restart this app";
                    await Task.Delay(5000);
                    IsLoadingModel = false;
                    return;
                }
                DevLog.WriteLine("OverlayViewModel: Ollama is running");
            }
            
            // Check if model is available
            LoadingStatusText = "Checking model...";
            LoadingSubText = $"Looking for {ModelName}";
            DevLog.WriteLine("OverlayViewModel: Checking if model {0} is available", ModelName);
            
            var available = await _ollama.IsModelAvailableAsync(ModelName, cts.Token);
            DevLog.WriteLine("OverlayViewModel: Model available: {0}", available);
            
            if (!available)
            {
                LoadingStatusText = "Downloading model...";
                LoadingSubText = $"Pulling {ModelName} - this may take a few minutes";
                DevLog.WriteLine("OverlayViewModel: Model not available, attempting to pull");
                
                if (_processManager != null)
                {
                    await _processManager.PullModelAsync(ModelName, cts.Token);
                }
            }
            
            // Warm up the model (load into memory) - this is the critical step
            LoadingStatusText = "Loading model into memory...";
            LoadingSubText = "First time takes 30-120 seconds depending on your PC...";
            DevLog.WriteLine("OverlayViewModel: Starting model warmup");
            
            // Try warmup with retries
            bool warmedUp = false;
            for (int i = 0; i < 3 && !warmedUp; i++)
            {
                try
                {
                    using var warmupCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                    warmedUp = await _ollama.WarmupModelAsync(ModelName, warmupCts.Token);
                    DevLog.WriteLine("OverlayViewModel: Warmup attempt {0} result: {1}", i + 1, warmedUp);
                }
                catch (Exception ex)
                {
                    DevLog.WriteLine("OverlayViewModel: Warmup attempt {0} failed: {1}", i + 1, ex.Message);
                    await Task.Delay(1000); // Brief pause before retry
                }
            }
            
            if (warmedUp)
            {
                // Verify the model is actually responding by doing a test chat
                LoadingStatusText = "Verifying...";
                LoadingSubText = "Running test query...";
                
                try
                {
                    using var verifyCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var testReply = await _ollama.ChatWithEmotionAsync("hi", AssistantName, "happy", null, verifyCts.Token);
                    DevLog.WriteLine("OverlayViewModel: Verification reply: {0}", testReply.Reply);
                }
                catch (Exception ex)
                {
                    DevLog.WriteLine("OverlayViewModel: Verification failed (non-critical): {0}", ex.Message);
                }
                
                LoadingStatusText = "Ready!";
                LoadingSubText = "Tsuki is awake and ready to chat~";
                await Task.Delay(500); // Brief pause to show "Ready!"
            }
            else
            {
                LoadingStatusText = "Model failed to load";
                LoadingSubText = "The model exists but won't load. Try:\n1. ollama rm " + ModelName + "\n2. ollama pull " + ModelName;
                await Task.Delay(5000);
            }
            
            IsLoadingModel = false;
            StatusText = "";
            
            // Send startup greeting now that we're ready
            if (!_startupGreetingSent)
            {
                SendStartupGreetingIfNeeded();
            }
        }
        catch (OperationCanceledException)
        {
            DevLog.WriteLine("OverlayViewModel: Warmup timed out after 5 minutes");
            LoadingStatusText = "Loading timed out";
            LoadingSubText = "Ollama is taking too long. Try:\n1. Restart Ollama: ollama serve\n2. Check if model is corrupted: ollama list\n3. Pull manually: ollama pull " + ModelName;
            await Task.Delay(5000);
            IsLoadingModel = false;
            StatusText = "Warmup timeout";
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("OverlayViewModel: Warmup failed - {0}", ex.Message);
            LoadingStatusText = "Error loading AI";
            LoadingSubText = $"Error: {ex.Message}";
            await Task.Delay(3000);
            IsLoadingModel = false;
            StatusText = "AI warmup failed";
        }
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
            value = string.IsNullOrWhiteSpace(value) ? "qwen2.5:3b" : value.Trim();
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

    public bool IsLoadingModel
    {
        get => _isLoadingModel;
        private set
        {
            if (value == _isLoadingModel) return;
            _isLoadingModel = value;
            OnPropertyChanged();
        }
    }

    public string LoadingStatusText
    {
        get => _loadingStatusText;
        private set
        {
            if (value == _loadingStatusText) return;
            _loadingStatusText = value;
            OnPropertyChanged();
        }
    }

    public string LoadingSubText
    {
        get => _loadingSubText;
        private set
        {
            if (value == _loadingSubText) return;
            _loadingSubText = value;
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

        // First try built-in commands. If not handled, fall back to Ollama.
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
            // Check if Ollama is reachable first
            StatusText = "Checking connection...";
            var isReachable = await _ollama.IsServerReachableAsync();
            if (!isReachable)
            {
                Emotion = "sad";
                AppendAssistant("Ollama is not running. Please start it first:\n1. Open terminal\n2. Run: ollama serve\n3. Or launch Ollama app");
                AddToContext("user", text);
                AddToContext("assistant", "Ollama not running");
                StatusText = "";
                return;
            }

            // Check if model is available
            StatusText = $"Checking model {_ollama.Model}...";
            var modelAvailable = await _ollama.IsModelAvailableAsync(_ollama.Model);
            if (!modelAvailable)
            {
                Emotion = "sad";
                AppendAssistant($"Model '{_ollama.Model}' not found. Pull it with:\nollama pull {_ollama.Model}");
                AddToContext("user", text);
                AddToContext("assistant", "Model not found");
                StatusText = "";
                return;
            }

            // Warn if model is still loading (not warmed up)
            if (!_ollama.IsWarmedUp)
            {
                StatusText = "Loading model (first time takes 30-60s)...";
            }

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

            // Use longer timeout for first request (model might still be loading)
            // First request can take 60-120s, subsequent ones are fast
            var timeout = _ollama.IsWarmedUp ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(180);
            _chatCts.CancelAfter(timeout);

            StartAssistantStream();
            
            // Try streaming first, fallback to non-streaming if it fails
            (string Reply, string Emotion) ai;
            try
            {
                ai = await _ollama.StreamChatAsync(
                    systemPrompt,
                    text,
                    historySnapshot,
                    partial => UpdateAssistantStream(partial),
                    _chatCts.Token
                );
            }
            catch (OperationCanceledException)
            {
                // If streaming times out, try non-streaming as fallback
                DevLog.WriteLine("Streaming timed out, trying fallback...");
                _chatCts = new CancellationTokenSource();
                _chatCts.CancelAfter(TimeSpan.FromSeconds(60));
                
                var reply = await _ollama.ChatWithEmotionAsync(
                    text,
                    AssistantName,
                    PreferredEmotion,
                    historySnapshot,
                    _chatCts.Token
                );
                ai = (reply.Reply, reply.Emotion);
                UpdateAssistantStream(ai.Reply); // Show the reply
            }

            Emotion = ai.Emotion;
            var finalReply = string.IsNullOrWhiteSpace(ai.Reply) ? "(no response from model)" : ai.Reply.Trim();
            finalReply = EnsureNonRepeating(finalReply);
            EndAssistantStream(finalReply);

            AddToContext("user", text);
            AddToContext("assistant", finalReply);
            TryCompressConversation();
        }
        catch (OperationCanceledException)
        {
            DevLog.WriteLine("Chat canceled or timeout");
            Emotion = "sad";
            _streaming = false;
            AppendAssistant("Request timed out. The model might be loading (first time takes 30-60s). Try again in a moment!");
            AddToContext("user", text);
            AddToContext("assistant", "Timeout - model loading");
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("Chat error: {0}", ex.Message);
            Emotion = "sad";
            _streaming = false;
            var reply = $"Ollama error: {ex.Message}\n\nMake sure:\n1. Ollama is running (ollama serve)\n2. Model is pulled: ollama pull {_ollama.Model}\n3. Your PC has enough RAM (3B model needs ~4GB)";
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

    private async void ProactiveTimerOnTick(object? sender, EventArgs e)
    {
        if (!_proactiveEnabled) return;
        // Safety check: Don't interrupt if thinking or streaming
        if (IsThinking || _streaming) return;

        var idleFor = DateTimeOffset.Now - _lastUserMessageAt;
        if (idleFor < _proactiveAfter) return;

        _lastUserMessageAt = DateTimeOffset.Now; // reset so we don't spam

        var nudge = "";
        
        // Try context-aware nudge first
        if (_windowCollector != null && _ollama.IsWarmedUp)
        {
            try 
            {
                var (process, title) = _windowCollector.GetActiveProcessAndTitle();
                if (!string.IsNullOrWhiteSpace(process))
                {
                    IsThinking = true; // Briefly show thinking state (optional, or remove if distracting) 
                    // Actually, for a background nudge, maybe don't show full "Thinking" UI to avoid stealing focus?
                    // Let's keep IsThinking=false or just set it briefly. 
                    // User said "safety function like where if chat bot is thinking...". 
                    // We shouldn't set IsThinking here if we want to stay "background", but we need the token.
                    
                    var system = _promptBuilder.BuildProactiveSystemPrompt(Emotion, TimeOfDayService.GetTimeOfDayTag());
                    var prompt = $"User is currently active in: {process} ({title}).\nContext: {AppConstants.OwnerName} has been quiet for {idleFor.TotalMinutes:F0} minutes.\nGenerate a short, casual, 1-sentence nudge based on this activity.";
                    
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var reply = await _ollama.ChatWithEmotionAsync(prompt, AssistantName, PreferredEmotion, null, cts.Token);
                    nudge = reply.Reply;
                }
            }
            catch 
            { 
                // Fallback to generic if generation fails
            }
            finally
            {
                IsThinking = false;
            }
        }

        if (string.IsNullOrWhiteSpace(nudge))
        {
            nudge = BuildProactiveNudge();
        }

        PostAssistantMessage(nudge, addToContext: true);
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

