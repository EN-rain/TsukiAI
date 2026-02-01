using System.Drawing;
using System.IO;
using Tesseract;

namespace TsukiAI.Desktop.Services.Ocr;

/// <summary>
/// OCR service that extracts text from screenshots using Tesseract OCR.
/// </summary>
public sealed class OcrService : IDisposable
{
    private TesseractEngine? _engine;
    private bool _isEnabled = true;
    private string _language = "eng";

    public bool IsEnabled => _isEnabled;
    public string Language => _language;
    public bool IsAvailable => _engine != null;

    public event Action<string>? TextExtracted;

    public OcrService(string language = "eng")
    {
        _language = language;
        InitializeEngine();
    }

    private void InitializeEngine()
    {
        try
        {
            // Look for tessdata in app directory or common locations
            var tessdataPath = FindTessDataPath();
            
            if (tessdataPath == null)
            {
                DevLog.WriteLine("OCR: tessdata not found. OCR will be disabled.");
                DevLog.WriteLine("OCR: Please download tessdata from https://github.com/tesseract-ocr/tessdata");
                _engine = null;
                return;
            }

            _engine = new TesseractEngine(tessdataPath, _language, EngineMode.Default);
            DevLog.WriteLine("OCR: Engine initialized with language: {0}", _language);
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("OCR: Error initializing engine - {0}", ex.Message);
            _engine = null;
        }
    }

    private string? FindTessDataPath()
    {
        // Check common locations
        var possiblePaths = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tessdata"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TsukiAI", "tessdata"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tesseract-OCR", "tessdata"),
            @"C:\Program Files\Tesseract-OCR\tessdata",
            @"C:\Program Files (x86)\Tesseract-OCR\tessdata",
        };

        foreach (var path in possiblePaths)
        {
            if (Directory.Exists(path) && File.Exists(Path.Combine(path, $"{_language}.traineddata")))
            {
                return path;
            }
        }

        // Return first existing path even if language file not found (will use default)
        foreach (var path in possiblePaths)
        {
            if (Directory.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    public void SetEnabled(bool enabled)
    {
        _isEnabled = enabled;
        DevLog.WriteLine("OCR: {0}", enabled ? "Enabled" : "Disabled");
    }

    public void SetLanguage(string language)
    {
        if (_language == language) return;
        
        _language = language;
        _engine?.Dispose();
        InitializeEngine();
    }

    /// <summary>
    /// Extract text from a bitmap image.
    /// </summary>
    public async Task<string> ExtractTextAsync(Bitmap bitmap)
    {
        if (!_isEnabled || _engine == null)
            return string.Empty;

        try
        {
            // Convert to format Tesseract can process
            using var pix = PixConverter.ToPix(bitmap);
            using var page = _engine.Process(pix);
            
            var extractedText = page.GetText();
            var confidence = page.GetMeanConfidence();
            
            if (!string.IsNullOrWhiteSpace(extractedText))
            {
                DevLog.WriteLine("OCR: Extracted {0} characters (confidence: {1:P0})", extractedText.Length, confidence);
                TextExtracted?.Invoke(extractedText);
            }
            
            return extractedText ?? string.Empty;
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("OCR: Error extracting text - {0}", ex.Message);
            return string.Empty;
        }
    }

    /// <summary>
    /// Extract text from a screenshot file.
    /// </summary>
    public async Task<string> ExtractTextFromFileAsync(string filePath)
    {
        if (!_isEnabled || _engine == null)
            return string.Empty;

        try
        {
            using var bitmap = new Bitmap(filePath);
            return await ExtractTextAsync(bitmap);
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("OCR: Error loading image - {0}", ex.Message);
            return string.Empty;
        }
    }

    /// <summary>
    /// Check if OCR is available on this system.
    /// </summary>
    public static bool IsOcrAvailable()
    {
        var service = new OcrService();
        var available = service.IsAvailable;
        service.Dispose();
        return available;
    }

    public void Dispose()
    {
        _engine?.Dispose();
    }
}
