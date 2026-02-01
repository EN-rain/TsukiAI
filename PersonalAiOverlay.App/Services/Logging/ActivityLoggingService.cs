using System.IO;
using PersonalAiOverlay.App.Models;
using PersonalAiOverlay.App.Services.Collectors;

namespace PersonalAiOverlay.App.Services.Logging;

public sealed class ActivityLoggingService : IDisposable
{
    private readonly ForegroundWindowCollector _foreground = new();
    private readonly IdleTimeProvider _idle = new();
    private readonly ScreenshotCapturer _screenshots = new();
    private readonly ActivitySampleStore _store = new();

    private CancellationTokenSource? _cts;
    private Task? _samplingLoop;
    private Task? _summaryLoop;

    private volatile bool _paused;

    public bool IsRunning => _cts is not null;
    public bool IsPaused => _paused;

    public event Action<bool, bool>? StateChanged; // (running, paused)
    public event Action<ActivitySample>? SampleCaptured;
    public event Action<string, DateTimeOffset, DateTimeOffset>? SummaryWritten; // (path, start, end)

    public Func<IReadOnlyList<ActivitySample>, CancellationToken, Task<string>>? Summarizer { get; set; }

    public void Start(AppSettings settings)
    {
        if (IsRunning) return;

        ActivityLogPaths.EnsureDirs();

        // Crash-safe cleanup (keep a buffer beyond the retention setting).
        var retention = TimeSpan.FromHours(Math.Max(1, settings.RetentionHoursForRaw) + 1);
        _store.CleanupRawOlderThan(retention);

        _paused = false;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _samplingLoop = Task.Run(() => SamplingLoopAsync(settings, ct), ct);
        _summaryLoop = Task.Run(() => SummaryLoopAsync(settings, ct), ct);
        StateChanged?.Invoke(true, _paused);
    }

    public void Stop()
    {
        if (!IsRunning) return;

        try { _cts?.Cancel(); } catch { }
        _cts = null;
        _paused = false;
        StateChanged?.Invoke(false, _paused);
    }

    public void Pause()
    {
        if (!IsRunning) return;
        _paused = true;
        StateChanged?.Invoke(true, _paused);
    }

    public void Resume()
    {
        if (!IsRunning) return;
        _paused = false;
        StateChanged?.Invoke(true, _paused);
    }

    private async Task SamplingLoopAsync(AppSettings settings, CancellationToken ct)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, settings.SampleIntervalMinutes));
        using var timer = new PeriodicTimer(interval);

        // Take an initial sample quickly after startup (but not at app launch microsecond).
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        await TryCaptureOnceAsync(settings, ct);

        while (await timer.WaitForNextTickAsync(ct))
        {
            await TryCaptureOnceAsync(settings, ct);
        }
    }

    private Task TryCaptureOnceAsync(AppSettings settings, CancellationToken ct)
    {
        if (_paused) return Task.CompletedTask;

        try
        {
            var ts = DateTimeOffset.Now;
            var (proc, title) = _foreground.GetActiveProcessAndTitle();
            var idleSeconds = _idle.GetIdleSeconds();

            var baseName = _store.GetSampleBaseName(ts);
            var jsonPath = _store.GetSampleJsonPath(baseName);
            var pngPath = _store.GetSampleScreenshotPath(baseName);

            string? screenshotPath = null;
            try
            {
                screenshotPath = _screenshots.CapturePng(pngPath, settings.CaptureMode);
            }
            catch
            {
                screenshotPath = null;
            }

            var sample = new ActivitySample(
                Timestamp: ts,
                ProcessName: proc,
                WindowTitle: title,
                IdleSeconds: idleSeconds,
                ScreenshotPath: screenshotPath
            );

            _store.SaveSample(sample, jsonPath);
            SampleCaptured?.Invoke(sample);
        }
        catch
        {
            // ignore capture failures
        }

        return Task.CompletedTask;
    }

    private async Task SummaryLoopAsync(AppSettings settings, CancellationToken ct)
    {
        var summarizeEvery = TimeSpan.FromMinutes(Math.Max(1, settings.SummarizeIntervalMinutes));
        using var timer = new PeriodicTimer(summarizeEvery);

        while (await timer.WaitForNextTickAsync(ct))
        {
            if (_paused) continue;

            try
            {
                await TrySummarizeOnceAsync(settings, ct);
            }
            catch
            {
                // ignore
            }
        }
    }

    private async Task TrySummarizeOnceAsync(AppSettings settings, CancellationToken ct)
    {
        var sampleInterval = Math.Max(1, settings.SampleIntervalMinutes);
        var summarizeInterval = Math.Max(1, settings.SummarizeIntervalMinutes);
        var sampleCount = Math.Max(1, summarizeInterval / sampleInterval);

        var jsonFiles = _store.ListSampleJsonFilesNewestFirst();
        var samples = new List<ActivitySample>();

        foreach (var f in jsonFiles.Take(sampleCount))
        {
            var s = _store.TryLoadSample(f);
            if (s is not null)
                samples.Add(s);
        }

        samples = samples
            .OrderBy(s => s.Timestamp)
            .ToList();

        if (samples.Count == 0)
            return;

        if (Summarizer is null)
            return;

        var periodStart = samples.First().Timestamp;
        var periodEnd = samples.Last().Timestamp;

        var summaryText = await Summarizer(samples, ct);
        if (string.IsNullOrWhiteSpace(summaryText))
            return;

        ActivityLogPaths.EnsureDirs();
        var fileName = $"summary-{DateTimeOffset.Now:yyyyMMdd-HH}.md";
        var summaryPath = Path.Combine(ActivityLogPaths.SummariesDir, fileName);

        var md =
            $"# Summary ({periodStart:yyyy-MM-dd HH:mm} → {periodEnd:yyyy-MM-dd HH:mm})\n\n"
            + summaryText.Trim()
            + "\n";

        File.WriteAllText(summaryPath, md);

        // Only delete raw after summary is written successfully.
        foreach (var s in samples)
            _store.DeleteSampleAndAssets(s);

        SummaryWritten?.Invoke(summaryPath, periodStart, periodEnd);
    }

    public void Dispose()
    {
        Stop();
        try { _cts?.Dispose(); } catch { }
    }
}

