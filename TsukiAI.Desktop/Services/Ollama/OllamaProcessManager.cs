using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace TsukiAI.Desktop.Services.Ollama;

/// <summary>
/// Manages Ollama process lifecycle and performance optimization.
/// Ensures model stays loaded for fast responses.
/// </summary>
public sealed class OllamaProcessManager : IDisposable
{
    private readonly System.Threading.Timer _keepAliveTimer;
    private readonly HttpClient _http;
    private readonly string _modelName;
    private readonly string? _modelDirectory;
    private bool _isWarmedUp = false;
    private readonly object _warmupLock = new();
    private Process? _ollamaProcess;
    private bool _serverRunning = false;

    public OllamaProcessManager(string modelName = "tsuki", string baseUrl = "http://localhost:11434", string? modelDirectory = null)
    {
        _modelName = modelName;
        _modelDirectory = modelDirectory;
        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
        
        // Ping every 90 seconds to keep model loaded (Ollama default timeout is 5 min)
        _keepAliveTimer = new System.Threading.Timer(async _ => await KeepAliveAsync(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(90));
    }

    /// <summary>
    /// Legacy API compatibility - redirects to EnsureRunningAsync
    /// </summary>
    public async Task EnsureServerAsync(CancellationToken ct = default, bool useGpu = true)
    {
        await EnsureRunningAsync(ct);
    }

    /// <summary>
    /// Check if Ollama server is running.
    /// </summary>
    public async Task<bool> IsServerRunningAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync("/api/tags", ct);
            _serverRunning = response.IsSuccessStatusCode;
            return _serverRunning;
        }
        catch
        {
            _serverRunning = false;
            return false;
        }
    }

    /// <summary>
    /// Pull a model from Ollama registry.
    /// </summary>
    public async Task PullModelAsync(string modelName, CancellationToken ct = default)
    {
        try
        {
            DevLog.WriteLine("OllamaProcessManager: Pulling model {0}...", modelName);
            var request = new { name = modelName };
            using var response = await _http.PostAsJsonAsync("/api/pull", request, ct);
            
            // Read the streaming response
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            
            while (!reader.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    DevLog.WriteLine("Ollama pull: {0}", line);
                }
            }
            
            DevLog.WriteLine("OllamaProcessManager: Model {0} pulled successfully", modelName);
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("OllamaProcessManager: Failed to pull model - {0}", ex.Message);
        }
    }

    /// <summary>
    /// Stop/unload a model to free memory.
    /// </summary>
    public async Task StopModelAsync(string modelName)
    {
        try
        {
            // Send an empty request to unload the model
            var request = new
            {
                model = modelName,
                messages = new[] { new { role = "user", content = "unload" } },
                stream = false,
                keep_alive = 0 // unload immediately
            };
            
            using var response = await _http.PostAsJsonAsync("/api/chat", request);
            DevLog.WriteLine("OllamaProcessManager: Model {0} unloaded", modelName);
            _isWarmedUp = false;
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("OllamaProcessManager: Failed to stop model - {0}", ex.Message);
        }
    }

    /// <summary>
    /// Ensures Ollama is running and model is loaded into memory.
    /// Call this on app startup.
    /// </summary>
    public async Task<bool> EnsureRunningAsync(CancellationToken ct = default)
    {
        try
        {
            // Check if Ollama API is reachable
            var response = await _http.GetAsync("/api/tags", ct);
            if (!response.IsSuccessStatusCode)
            {
                // Try to start Ollama
                if (!await TryStartOllamaAsync(ct))
                    return false;
            }

            _serverRunning = true;
            
            // Warm up the model with a minimal request
            await WarmupModelAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("OllamaProcessManager: Failed to ensure running - {0}", ex.Message);
            return false;
        }
    }

    private async Task<bool> TryStartOllamaAsync(CancellationToken ct)
    {
        try
        {
            // Use cmd.exe /c to run ollama serve properly on Windows
            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c ollama serve",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            // Set environment variables for maximum performance
            // Use all CPU cores
            var cpuCount = Environment.ProcessorCount;
            startInfo.EnvironmentVariables["OLLAMA_NUM_THREADS"] = cpuCount.ToString();
            
            // Keep model loaded for 24 hours (in seconds)
            startInfo.EnvironmentVariables["OLLAMA_KEEP_ALIVE"] = "86400";
            
            // Allow multiple models if needed
            startInfo.EnvironmentVariables["OLLAMA_MAX_LOADED_MODELS"] = "1";
            
            // GPU offloading - let Ollama decide optimal layers
            // Set to high number to offload as much as possible
            startInfo.EnvironmentVariables["OLLAMA_GPU_LAYERS"] = "999";
            
            // Set custom model directory if specified
            if (!string.IsNullOrWhiteSpace(_modelDirectory))
            {
                startInfo.EnvironmentVariables["OLLAMA_MODELS"] = _modelDirectory;
                DevLog.WriteLine("OllamaProcessManager: Using custom model directory: {0}", _modelDirectory);
            }

            DevLog.WriteLine("OllamaProcessManager: Starting ollama serve via cmd.exe...");
            _ollamaProcess = Process.Start(startInfo);
            if (_ollamaProcess == null) return false;

            // Wait for Ollama to be ready
            await Task.Delay(2000, ct);
            
            // Poll until ready (up to 30 seconds)
            for (int i = 0; i < 60; i++)
            {
                try
                {
                    var response = await _http.GetAsync("/api/tags", ct);
                    if (response.IsSuccessStatusCode)
                    {
                        DevLog.WriteLine("OllamaProcessManager: Started ollama serve with {0} threads", cpuCount);
                        return true;
                    }
                }
                catch { }
                await Task.Delay(500, ct);
            }
            
            DevLog.WriteLine("OllamaProcessManager: Timeout waiting for Ollama to start");
            return false;
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("OllamaProcessManager: Failed to start Ollama - {0}", ex.Message);
            return false;
        }
    }

    private async Task WarmupModelAsync(CancellationToken ct)
    {
        lock (_warmupLock)
        {
            if (_isWarmedUp) return;
        }

        try
        {
            var warmupRequest = new
            {
                model = _modelName,
                messages = new[]
                {
                    new { role = "system", content = "Warmup." },
                    new { role = "user", content = "Hi" }
                },
                stream = false,
                options = new
                {
                    num_predict = 5,
                    temperature = 0.1
                },
                keep_alive = 86400
            };

            using var response = await _http.PostAsJsonAsync("/api/chat", warmupRequest, ct);
            
            if (response.IsSuccessStatusCode)
            {
                lock (_warmupLock)
                {
                    _isWarmedUp = true;
                }
                DevLog.WriteLine("OllamaProcessManager: Model {0} warmed up successfully", _modelName);
            }
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("OllamaProcessManager: Warmup failed - {0}", ex.Message);
        }
    }

    private async Task KeepAliveAsync()
    {
        if (!_serverRunning) return;
        
        try
        {
            // Simple request to keep model in memory
            var keepAliveRequest = new
            {
                model = _modelName,
                messages = new[]
                {
                    new { role = "user", content = "ping" }
                },
                stream = false,
                options = new
                {
                    num_predict = 1
                },
                keep_alive = 86400
            };

            using var response = await _http.PostAsJsonAsync("/api/chat", keepAliveRequest);
            DevLog.WriteLine("OllamaProcessManager: Keep-alive ping sent");
        }
        catch (Exception ex)
        {
            DevLog.WriteLine("OllamaProcessManager: Keep-alive failed - {0}", ex.Message);
            // Try to recover
            _isWarmedUp = false;
            _serverRunning = false;
        }
    }

    public void Dispose()
    {
        _keepAliveTimer?.Dispose();
        _http?.Dispose();
        try { _ollamaProcess?.Kill(); } catch { }
        _ollamaProcess?.Dispose();
    }
}
