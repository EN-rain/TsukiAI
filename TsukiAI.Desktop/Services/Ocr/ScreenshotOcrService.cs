using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Timers;
using TsukiAI.Desktop.Services.Collectors;
using TsukiAI.Desktop.Interop;
using Timer = System.Timers.Timer;

namespace TsukiAI.Desktop.Services.Ocr;

/// <summary>
/// Service that periodically captures screenshots and extracts text via OCR.
/// Replaces the old ActivityLoggingService.
/// </summary>
public sealed class ScreenshotOcrService : IDisposable
{
    private readonly ScreenshotCapturer _capturer;
    private readonly OcrService _ocrService;
    private readonly ForegroundWindowCollector _windowCollector;
    private readonly Timer _captureTimer;
    private readonly ConcurrentQueue<OcrResult> _ocrQueue;
    private readonly CancellationTokenSource _cts;
    private Task? _ocrWorker;
    
    private bool _isRunning;
    private string _saveDirectory;
    private int _captureIntervalMs = 30000; // 30 seconds default

    public bool IsRunning => _isRunning;
    public string SaveDirectory => _saveDirectory;
    public int CaptureIntervalSeconds => _captureIntervalMs / 1000;

    public event Action<OcrResult>? OcrCompleted;
    public event Action<string>? StatusChanged;

    public ScreenshotOcrService(OcrService ocrService, string? saveDirectory = null)
    {
        _ocrService = ocrService;
        _capturer = new ScreenshotCapturer();
        _windowCollector = new ForegroundWindowCollector();
        _ocrQueue = new ConcurrentQueue<OcrResult>();
        _cts = new CancellationTokenSource();
        
        _saveDirectory = saveDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TsukiAI",
            "Screenshots"
        );
        
        Directory.CreateDirectory(_saveDirectory);
        
        _captureTimer = new Timer();
        _captureTimer.Elapsed += OnCaptureTimerElapsed;
        _captureTimer.AutoReset = true;
    }

    public void Start(int intervalSeconds = 30)
    {
        if (_isRunning) return;
        
        _captureIntervalMs = intervalSeconds * 1000;
        _captureTimer.Interval = _captureIntervalMs;
        _isRunning = true;
        
        _captureTimer.Start();
        _ocrWorker = Task.Run(ProcessOcrQueueAsync);
        
        StatusChanged?.Invoke("OCR: Running");
        DevLog.WriteLine("ScreenshotOCR: Started with {0}s interval", intervalSeconds);
    }

    public void Stop()
    {
        if (!_isRunning) return;
        
        _isRunning = false;
        _captureTimer.Stop();
        
        StatusChanged?.Invoke("OCR: Stopped");
        DevLog.WriteLine("ScreenshotOCR: Stopped");
    }

    public void SetInterval(int seconds)
    {
        _captureIntervalMs = seconds * 1000;
        if (_isRunning)
        {
            _captureTimer.Interval = _captureIntervalMs;
        }
        DevLog.WriteLine("ScreenshotOCR: Interval set to {0}s", seconds);
    }

    private async void OnCaptureTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        if (!_ocrService.IsEnabled) return;

        try
        {
            // Capture screenshot
            using var screenshot = _capturer.CaptureFullScreen();
            
            // Get window info
            var (processName, windowTitle) = _windowCollector.GetActiveProcessAndTitle();
            
            // Save screenshot
            var timestamp = DateTime.Now;
            var fileName = $"screenshot_{timestamp:yyyyMMdd_HHmmss}.png";
            var filePath = Path.Combine(_saveDirectory, fileName);
            screenshot.Save(filePath, System.Drawing.Imaging.ImageFormat.Png);
            
            // Queue for OCR processing
            var result = new OcrResult
            {
                Timestamp = timestamp,
                ScreenshotPath = filePath,
                ProcessName = processName,
                WindowTitle = windowTitle
            };
            
            _ocrQueue.Enqueue(result);
            
            // Clean old screenshots (keep last 100)
            CleanupOldScreenshots();
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("ScreenshotOCR: Capture error - {0}", ex.Message);
        }
    }

    private async Task ProcessOcrQueueAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            if (_ocrQueue.TryDequeue(out var result))
            {
                try
                {
                    StatusChanged?.Invoke("OCR: Processing...");
                    
                    // Extract text via OCR
                    var extractedText = await _ocrService.ExtractTextFromFileAsync(result.ScreenshotPath);
                    
                    result.ExtractedText = extractedText;
                    result.IsProcessed = true;
                    
                    if (!string.IsNullOrWhiteSpace(extractedText))
                    {
                        OcrCompleted?.Invoke(result);
                        DevLog.WriteLine("ScreenshotOCR: Processed - {0} chars from {1}", 
                            extractedText.Length, result.ProcessName);
                    }
                    
                    StatusChanged?.Invoke("OCR: Idle");
                }
                catch (Exception ex)
                {
                    DevLog.WriteLine("ScreenshotOCR: Processing error - {0}", ex.Message);
                }
            }
            else
            {
                await Task.Delay(100, _cts.Token);
            }
        }
    }

    private void CleanupOldScreenshots()
    {
        try
        {
            var files = new DirectoryInfo(_saveDirectory)
                .GetFiles("screenshot_*.png")
                .OrderByDescending(f => f.CreationTime)
                .Skip(100)
                .ToList();
            
            foreach (var file in files)
            {
                try { file.Delete(); } catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// Get recent OCR results.
    /// </summary>
    public List<OcrResult> GetRecentResults(int count = 10)
    {
        try
        {
            return new DirectoryInfo(_saveDirectory)
                .GetFiles("screenshot_*.png")
                .OrderByDescending(f => f.CreationTime)
                .Take(count)
                .Select(f => new OcrResult
                {
                    Timestamp = f.CreationTime,
                    ScreenshotPath = f.FullName
                })
                .ToList();
        }
        catch
        {
            return new List<OcrResult>();
        }
    }

    public void Dispose()
    {
        Stop();
        _cts.Cancel();
        _captureTimer.Dispose();
        _cts.Dispose();
    }
}

/// <summary>
/// Result of an OCR operation.
/// </summary>
public sealed class OcrResult
{
    public DateTime Timestamp { get; set; }
    public string ScreenshotPath { get; set; } = "";
    public string? ProcessName { get; set; }
    public string? WindowTitle { get; set; }
    public string? ExtractedText { get; set; }
    public bool IsProcessed { get; set; }
}
