using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace TsukiAI.Desktop;

public partial class DiagnosticsWindow : Window
{
    private readonly HttpClient _http;
    private readonly StringBuilder _log = new();
    private readonly Stopwatch _stopwatch = new();
    private readonly List<double> _responseTimes = new();

    private readonly string _modelName;

    public DiagnosticsWindow(string modelName)
    {
        _modelName = modelName;
        InitializeComponent();
        _http = new HttpClient { BaseAddress = new Uri("http://localhost:11434"), Timeout = TimeSpan.FromSeconds(60) };
        Loaded += DiagnosticsWindow_Loaded;
    }

    private async void DiagnosticsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshStatusAsync();
    }

    private async Task RefreshStatusAsync()
    {
        try
        {
            // Check Ollama server
            Log("Checking Ollama server...");
            var sw = Stopwatch.StartNew();
            var response = await _http.GetAsync("/api/tags");
            sw.Stop();
            
            OllamaStatusText.Text = response.IsSuccessStatusCode 
                ? $"Running ({sw.ElapsedMilliseconds}ms)" 
                : "Not responding";
            Log($"Ollama API: {response.StatusCode} in {sw.ElapsedMilliseconds}ms");

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(content);
                var models = doc.RootElement.GetProperty("models");
                
                Log($"Models found: {models.GetArrayLength()}");
                foreach (var model in models.EnumerateArray())
                {
                    var name = model.GetProperty("name").GetString();
                    var size = model.TryGetProperty("size", out var sizeProp) ? sizeProp.GetInt64() : 0;
                    Log($"  - {name} ({size / 1024 / 1024}MB)");
                }
            }
        }
        catch (Exception ex)
        {
            OllamaStatusText.Text = "Error";
            Log($"Ollama check failed: {ex.Message}");
        }

        try
        {
            // Check running models
            Log("\nChecking loaded models...");
            var psResponse = await _http.GetAsync("/api/ps");
            if (psResponse.IsSuccessStatusCode)
            {
                var content = await psResponse.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(content);
                var models = doc.RootElement.GetProperty("models");
                
                if (models.GetArrayLength() == 0)
                {
                    ModelStatusText.Text = "No models loaded";
                    MemoryText.Text = "0 MB";
                    Log("WARNING: No models currently loaded in memory!");
                    Log("Click 'Force Load Model' to load the model, or:");
                    Log("  1. Open Command Prompt");
                    Log("  2. Run: ollama run " + _modelName);
                    Log("  3. Then close the chat (model stays loaded)");
                }
                else
                {
                    foreach (var model in models.EnumerateArray())
                    {
                        var name = model.GetProperty("name").GetString();
                        var size = model.GetProperty("size").GetInt64();
                        var sizeVram = model.TryGetProperty("size_vram", out var vramProp) ? vramProp.GetInt64() : 0;
                        
                        ModelStatusText.Text = $"{name} (loaded)";
                        MemoryText.Text = $"{size / 1024 / 1024} MB (VRAM: {sizeVram / 1024 / 1024} MB)";
                        Log($"Model loaded: {name}");
                        Log($"  Memory: {size / 1024 / 1024}MB (VRAM: {sizeVram / 1024 / 1024}MB)");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ModelStatusText.Text = "Error";
            Log($"Model check failed: {ex.Message}");
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        Log("\nRefreshing status...");
        await RefreshStatusAsync();
    }

    private async void TestDirectApi_Click(object sender, RoutedEventArgs e)
    {
        Log("\n=== Testing Direct API Call ===");
        
        try
        {
            var request = new
            {
                model = _modelName,
                messages = new[]
                {
                    new { role = "user", content = "Say 'hi' in one word" }
                },
                stream = false,
                options = new
                {
                    num_predict = 10,
                    temperature = 0.1
                }
            };

            Log("Sending request...");
            _stopwatch.Restart();
            var response = await _http.PostAsJsonAsync("/api/chat", request);
            var firstTokenMs = _stopwatch.ElapsedMilliseconds;
            
            var content = await response.Content.ReadAsStringAsync();
            _stopwatch.Stop();
            
            var totalMs = _stopwatch.ElapsedMilliseconds;
            _responseTimes.Add(totalMs);
            
            RequestTimeText.Text = $"{totalMs}ms (first token: {firstTokenMs}ms)";
            AvgTimeText.Text = $"{_responseTimes.Average():F0}ms (n={_responseTimes.Count})";
            
            Log($"Response time: {totalMs}ms");
            Log($"First token: {firstTokenMs}ms");
            
            if (response.IsSuccessStatusCode)
            {
                var doc = JsonDocument.Parse(content);
                if (doc.RootElement.TryGetProperty("message", out var msg) &&
                    msg.TryGetProperty("content", out var contentProp))
                {
                    var reply = contentProp.GetString();
                    Log($"Response: '{reply}'");
                }
                
                // Check if model loaded during request
                if (firstTokenMs > 5000)
                {
                    Log("WARNING: First token took >5s - model was likely not loaded!");
                }
            }
            else
            {
                Log($"Error: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Log($"Test failed: {ex.Message}");
        }
    }

    private async void ForceLoad_Click(object sender, RoutedEventArgs e)
    {
        Log("\n=== Force Loading Model ===");
        
        try
        {
            var request = new
            {
                model = _modelName,
                messages = new[]
                {
                    new { role = "user", content = "ping" }
                },
                stream = false,
                keep_alive = 86400, // Keep loaded for 24 hours
                options = new
                {
                    num_predict = 1
                }
            };

            Log($"Loading model: {_modelName}");
            Log("This may take 30-60s on first load...");
            _stopwatch.Restart();
            var response = await _http.PostAsJsonAsync("/api/chat", request);
            _stopwatch.Stop();
            
            if (response.IsSuccessStatusCode)
            {
                Log($"SUCCESS! Model loaded in {_stopwatch.ElapsedMilliseconds}ms");
                Log("Model will stay loaded for 24 hours.");
            }
            else
            {
                Log($"Failed: {response.StatusCode}");
                Log("Try manual start:");
                Log($"  ollama run {_modelName}");
            }
            
            // Refresh status
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            Log($"Force load failed: {ex.Message}");
            Log("Try manual start:");
            Log("  1. Open Command Prompt");
            Log($"  2. Run: ollama run {_modelName}");
            Log("  3. Type 'bye' and press Enter (model stays loaded)");
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _log.Clear();
        LogText.Text = "";
    }

    private void Log(string message)
    {
        _log.AppendLine($"[{DateTime.Now:HH:mm:ss}] {message}");
        LogText.Text = _log.ToString();
        LogScroll.ScrollToEnd();
    }
}
