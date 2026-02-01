using System.Windows;
using System.IO;
using System.Threading.Channels;
using System.Windows.Forms;
using System.Drawing;
using PersonalAiOverlay.App.Models;
using PersonalAiOverlay.App.Services;
using PersonalAiOverlay.App.Services.Logging;
using PersonalAiOverlay.App.Services.Ollama;
using PersonalAiOverlay.App.ViewModels;
using PersonalAiOverlay.Core.Assistant;
using Application = System.Windows.Application;

namespace PersonalAiOverlay.App;

public partial class App : System.Windows.Application
{
    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private bool _quitRequested;

    private ActivityLoggingService? _activityLogger;
    private OllamaProcessManager? _ollamaProcess;
    private OllamaClient? _ollamaClient;
    private CancellationTokenSource? _lifetimeCts;
    private bool _pollInFlight;
    private AppSettings _currentSettings = AppSettings.Default;
    private Channel<ActivitySample>? _fiveMinChannel;
    private Task? _fiveMinWorker;
    private MemoryStore? _memoryStore;
    private PromptBuilder? _promptBuilder;
    private readonly List<ActivitySample> _recentSamples = new();
    private readonly object _recentSamplesLock = new();
    private EmotionStateMachine? _emotionStateMachine;
    private CooldownService? _cooldownService;
    private ReactionTemplates? _reactionTemplates;
    private PersonalityBiasService? _personalityBiasService;

    public void OnStartup(object sender, StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        DevLog.WriteLine("App started");
        var settings = SettingsService.Load();
        _currentSettings = settings;
        DevLog.WriteLine("Settings loaded: Model={0}, ActivityLog={1}", settings.ModelName, settings.IsActivityLoggingEnabled);

        var deps = new AssistantEngineDependencies(
            Clock: new PlatformClock(),
            UrlOpener: new UrlOpener(),
            Clipboard: new WpfClipboard()
        );

        var engine = new AssistantEngine(deps);
        _ollamaClient = new OllamaClient(model: settings.ModelName);
        _memoryStore = new MemoryStore(SettingsService.GetBaseDir());
        var memoryRules = new MemoryRules();
        _promptBuilder = new PromptBuilder();
        var baseDir = SettingsService.GetBaseDir();
        _emotionStateMachine = new EmotionStateMachine();
        _cooldownService = new CooldownService();
        _reactionTemplates = new ReactionTemplates(baseDir);
        _personalityBiasService = new PersonalityBiasService(baseDir);
        var vm = new OverlayViewModel(engine, _ollamaClient, _memoryStore, memoryRules, _promptBuilder, _personalityBiasService);
        vm.ModelName = settings.ModelName;

        var window = new MainWindow(vm);
        MainWindow = window;
        window.Show();

        SetupTrayIcon(window);

        vm.AssistantReplied += reply =>
        {
            if (_notifyIcon is null) return;
            var main = MainWindow;
            if (main?.IsActive == true) return;
            var text = (reply ?? "").Trim();
            if (text.Length == 0) return;
            if (text.Length > 120) text = text[..117] + "...";
            Dispatcher.Invoke(() =>
            {
                try
                {
                    _notifyIcon.BalloonTipTitle = "Tsuki";
                    _notifyIcon.BalloonTipText = text;
                    _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
                    _notifyIcon.ShowBalloonTip(4000);
                }
                catch { }
            });
        };

        _lifetimeCts = new CancellationTokenSource();

        // Startup greeting + proactive check-ins (idle-based).
        if (settings.StartupGreetingEnabled)
            vm.SendStartupGreetingIfNeeded();
        vm.ConfigureProactiveMessages(settings.ProactiveMessagesEnabled, settings.ProactiveMessageAfterMinutes);

        _fiveMinChannel = Channel.CreateUnbounded<ActivitySample>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        // Ollama: auto-start + (optional) pull model in background.
        _ollamaProcess = new OllamaProcessManager();
        if (settings.AutoStartOllama)
        {
            _ = Task.Run(async () =>
            {
                vm.OllamaStatus = "Ollama: starting…";
                await _ollamaProcess.EnsureServerAsync(_lifetimeCts.Token, settings.UseGpu);
                vm.OllamaStatus = await _ollamaProcess.IsServerRunningAsync(_lifetimeCts.Token) ? "Ollama: ON" : "Ollama: OFF";

                // If model is not present locally, start pulling (can take a while).
                if (_ollamaClient is not null)
                {
                    var available = await _ollamaClient.IsModelAvailableAsync(settings.ModelName, _lifetimeCts.Token);
                    if (!available)
                    {
                        vm.ModelStatus = "Model: pulling…";
                        await _ollamaProcess.PullModelAsync(settings.ModelName, _lifetimeCts.Token);
                    }

                    // Warm up (load model into memory) so you don't have to do it manually.
                    vm.ModelStatus = "Model: loading…";
                    await _ollamaClient.WarmupModelAsync(settings.ModelName, _lifetimeCts.Token);
                }
            });
        }

        // Activity logging service wiring
        _activityLogger = new ActivityLoggingService();
        _activityLogger.Summarizer = async (samples, ct) =>
        {
            if (_ollamaClient is null) return "";
            return await _ollamaClient.SummarizeActivityAsync(samples, vm.AssistantName, ct);
        };
        _activityLogger.StateChanged += (running, paused) =>
        {
            Dispatcher.Invoke(() =>
            {
                vm.ActivityLoggingEnabled = running;
            });
        };
        _activityLogger.SampleCaptured += sample =>
        {
            lock (_recentSamplesLock)
            {
                _recentSamples.Add(sample);
                while (_recentSamples.Count > 20) _recentSamples.RemoveAt(0);
            }
            Dispatcher.Invoke(vm.OnSampleTick);
            Dispatcher.Invoke(() => vm.SetLastCaptureAt(sample.Timestamp));
            _fiveMinChannel?.Writer.TryWrite(sample);
        };
        _activityLogger.SummaryWritten += (_, _, _) =>
        {
            Dispatcher.Invoke(vm.ResetTicks);
        };
        _activityLogger.SummaryWritten += (path, start, end) =>
        {
            _ = Task.Run(() =>
            {
                try
                {
                    var text = File.ReadAllText(path).Trim();
                    Dispatcher.Invoke(() => vm.AppendHourlySummary(text));

                    // Add a short Tsuki reaction after the summary (cooldown-gated).
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (_ollamaClient is null || _cooldownService is null || _promptBuilder is null) return;
                            var emotion = _emotionStateMachine?.CurrentEmotion ?? vm.Emotion;
                            if (!_cooldownService.CanSpeak(emotion)) return;
                            var timeOfDay = TimeOfDayService.GetTimeOfDayTag();
                            var systemPrompt = _promptBuilder.BuildHourlyReactionSystemPrompt(emotion, timeOfDay);
                            var reaction = await _ollamaClient.ReactToHourlySummaryAsync(text, systemPrompt, _lifetimeCts?.Token ?? CancellationToken.None);
                            if (!string.IsNullOrWhiteSpace(reaction))
                            {
                                Dispatcher.Invoke(() => vm.AppendHourlySummary("Tsuki: " + reaction));
                                _cooldownService.RecordSpoke();
                            }
                        }
                        catch { }
                    });
                }
                catch
                {
                    var fallback = $"# Summary ({start:yyyy-MM-dd HH:mm} → {end:yyyy-MM-dd HH:mm})\n\n(Unable to read summary file.)";
                    Dispatcher.Invoke(() => vm.AppendHourlySummary(fallback));
                }
            });
        };

        if (settings.IsActivityLoggingEnabled)
            _activityLogger.Start(settings);

        // Background worker: sequentially handle 5-minute summaries (event summarizer + cooldown + templates).
        _fiveMinWorker = Task.Run(() => ProcessFiveMinuteSummariesAsync(vm, _lifetimeCts.Token));

        // Learning over time
        _memoryStore.ApplyDecay();
        var learner = new MemoryLearner(_memoryStore, _ollamaClient);
        _ = Task.Run(() => learner.RunDailyLearningAsync(_lifetimeCts.Token));
        _ = Task.Run(() => learner.RunWeeklyLearningAsync(_lifetimeCts.Token));

        // Status polling for model/metrics.
        var metrics = new SystemUsageMonitor();
        var pollTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        pollTimer.Tick += async (_, _) =>
        {
            if (_pollInFlight) return;
            _pollInFlight = true;

            var (cpu, mem) = metrics.GetUsage();
            vm.CpuPercent = cpu;
            vm.MemoryMb = mem;

            try
            {
                if (_ollamaClient is null) return;
                var serverOn = _ollamaProcess is not null && await _ollamaProcess.IsServerRunningAsync();
                vm.OllamaStatus = serverOn ? "Ollama: ON" : "Ollama: OFF";

                if (serverOn)
                {
                    var available = await _ollamaClient.IsModelAvailableAsync(vm.ModelName);
                    var running = await _ollamaClient.IsModelRunningAsync(vm.ModelName);
                    vm.ModelStatus = running ? "Model: loaded" : (available ? "Model: ready" : "Model: missing");
                }
                else
                {
                    vm.ModelStatus = "Model: ?";
                }
            }
            catch
            {
                vm.OllamaStatus = "Ollama: ?";
                vm.ModelStatus = "Model: ?";
            }
            finally
            {
                _pollInFlight = false;
            }
        };
        pollTimer.Start();

        // Countdown timer to next capture (1s granularity).
        var captureTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        captureTimer.Tick += (_, _) =>
        {
            var interval = TimeSpan.FromMinutes(Math.Max(1, _currentSettings.SampleIntervalMinutes));
            vm.UpdateNextCaptureCountdown(interval);
        };
        captureTimer.Start();
    }

    private void SetupTrayIcon(Window mainWindow)
    {
        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "Personal AI (Tsuki)",
            Visible = true
        };

        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                using var ico = Icon.ExtractAssociatedIcon(exePath);
                if (ico is not null)
                    _notifyIcon.Icon = (Icon)ico.Clone();
            }
        }
        catch { }

        if (_notifyIcon.Icon is null)
            _notifyIcon.Icon = SystemIcons.Application;

        var showItem = new ToolStripMenuItem("Show");
        showItem.Click += (_, _) =>
        {
            mainWindow.Dispatcher.Invoke(() =>
            {
                mainWindow.Show();
                mainWindow.WindowState = WindowState.Normal;
                mainWindow.Activate();
            });
        };

        var quitItem = new ToolStripMenuItem("Quit");
        quitItem.Click += (_, _) => RequestQuit();

        _notifyIcon.ContextMenuStrip = new ContextMenuStrip();
        _notifyIcon.ContextMenuStrip.Items.Add(showItem);
        _notifyIcon.ContextMenuStrip.Items.Add(quitItem);

        _notifyIcon.DoubleClick += (_, _) => showItem.PerformClick();
    }

    /// <summary>Called when user chooses Quit from tray. MainWindow.Closing checks this and does not cancel.</summary>
    public bool QuitRequested => _quitRequested;

    public void RequestQuit()
    {
        _quitRequested = true;
        MainWindow?.Dispatcher.Invoke(() => MainWindow?.Close());
    }

    public void ApplySettings(AppSettings settings, OverlayViewModel vm)
    {
        SettingsService.Save(settings);
        _currentSettings = settings;

        vm.ModelName = settings.ModelName;
        vm.ConfigureProactiveMessages(settings.ProactiveMessagesEnabled, settings.ProactiveMessageAfterMinutes);

        _ollamaClient?.SetModel(settings.ModelName);

        if (_activityLogger is not null)
        {
            if (settings.IsActivityLoggingEnabled)
            {
                if (!_activityLogger.IsRunning)
                    _activityLogger.Start(settings);
            }
            else
            {
                if (_activityLogger.IsRunning)
                    _activityLogger.Stop();
            }
        }

        // Start server if newly enabled
        if (settings.AutoStartOllama)
        {
            _ = Task.Run(async () =>
            {
                if (_ollamaProcess is null) return;
                await _ollamaProcess.EnsureServerAsync(_lifetimeCts?.Token ?? CancellationToken.None, settings.UseGpu);
            });
        }
    }

    public void OnExit(object sender, ExitEventArgs e)
    {
        try { _lifetimeCts?.Cancel(); } catch { }

        try { _activityLogger?.Dispose(); } catch { }
        _activityLogger = null;

        try { _fiveMinChannel?.Writer.TryComplete(); } catch { }

        if (_currentSettings.StopOllamaOnExit)
        {
            try
            {
                // Unload the model (does not kill the system-wide daemon if one is running).
                var t = _ollamaProcess?.StopModelAsync(_currentSettings.ModelName);
                if (t is not null)
                {
                    // best-effort short wait
                    Task.WaitAny(t, Task.Delay(1200));
                }
            }
            catch { }
        }

        try { _ollamaProcess?.Dispose(); } catch { }
        _ollamaProcess = null;

        try { _notifyIcon?.Dispose(); } catch { }
        _notifyIcon = null;
    }

    private static string GetEventTypeFromSummary(string summaryLine)
    {
        var s = (summaryLine ?? "").ToLowerInvariant();
        if (s.Contains("idle")) return "idle_long";
        if (s.Contains("code")) return "long_coding_session";
        if (s.Contains("switching") || s.Contains("alternating")) return "switching_frequently";
        return "general";
    }

    private async Task ProcessFiveMinuteSummariesAsync(OverlayViewModel vm, CancellationToken ct)
    {
        if (_fiveMinChannel is null || _emotionStateMachine is null || _cooldownService is null || _reactionTemplates is null || _promptBuilder is null)
            return;

        await foreach (var sample in _fiveMinChannel.Reader.ReadAllAsync(ct))
        {
            try
            {
                List<ActivitySample> copy;
                lock (_recentSamplesLock)
                    copy = new List<ActivitySample>(_recentSamples);

                var summaryLine = EventSummarizer.Summarize(copy);
                _emotionStateMachine.Update(copy, sample.IdleSeconds, 0);
                var emotion = _emotionStateMachine.CurrentEmotion;
                var timeOfDay = TimeOfDayService.GetTimeOfDayTag();

                var displayLine = $"{sample.Timestamp:HH:mm} — {summaryLine}";
                Dispatcher.Invoke(() => vm.AppendFiveMinuteSummary(displayLine));

                if (_ollamaClient is null) continue;
                if (!_cooldownService.CanSpeak(emotion)) continue;

                var eventType = GetEventTypeFromSummary(summaryLine);
                var templateLine = _reactionTemplates.TryGetLine(eventType, emotion);
                if (templateLine is not null)
                {
                    Dispatcher.Invoke(() => vm.AppendFiveMinuteSummary("Tsuki: " + templateLine));
                    _cooldownService.RecordSpoke();
                    continue;
                }

                var systemPrompt = _promptBuilder.BuildFiveMinuteSystemPrompt(emotion, timeOfDay);
                var reaction = await _ollamaClient.ReactToSummaryAsync(summaryLine, systemPrompt, ct);
                if (!string.IsNullOrWhiteSpace(reaction))
                {
                    Dispatcher.Invoke(() => vm.AppendFiveMinuteSummary("Tsuki: " + reaction));
                    _cooldownService.RecordSpoke();
                }
            }
            catch
            {
                List<ActivitySample> copy;
                lock (_recentSamplesLock)
                    copy = new List<ActivitySample>(_recentSamples);
                var fallback = $"{sample.Timestamp:HH:mm} — {EventSummarizer.Summarize(copy)}";
                Dispatcher.Invoke(() => vm.AppendFiveMinuteSummary(fallback));
            }
        }
    }
}

