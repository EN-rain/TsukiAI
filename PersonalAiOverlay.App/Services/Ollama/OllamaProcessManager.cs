using System.Diagnostics;
using System.Net.Http;

namespace PersonalAiOverlay.App.Services.Ollama;

public sealed class OllamaProcessManager : IDisposable
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri("http://localhost:11434") };
    private Process? _serveProcess;
    private DateTimeOffset _lastPingAt = DateTimeOffset.MinValue;
    private bool _lastPingOk;

    public bool StartedByApp => _serveProcess is not null;

    public async Task<bool> IsServerRunningAsync(CancellationToken ct = default)
    {
        try
        {
            // Cache ping briefly to avoid spamming /api/version.
            if (DateTimeOffset.Now - _lastPingAt < TimeSpan.FromSeconds(2))
                return _lastPingOk;

            using var resp = await _http.GetAsync("/api/version", ct);
            _lastPingOk = resp.IsSuccessStatusCode;
            _lastPingAt = DateTimeOffset.Now;
            return _lastPingOk;
        }
        catch
        {
            _lastPingOk = false;
            _lastPingAt = DateTimeOffset.Now;
            return false;
        }
    }

    public async Task EnsureServerAsync(CancellationToken ct = default, bool useGpu = true)
    {
        if (await IsServerRunningAsync(ct))
            return;

        // Start a local server process (only if not already reachable).
        // NOTE: If Ollama is installed as a service/daemon, this won't be used.
        var psi = new ProcessStartInfo
        {
            FileName = "ollama",
            Arguments = "serve",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (!useGpu)
            psi.Environment["OLLAMA_NUM_GPU"] = "0";

        _serveProcess = Process.Start(psi);

        // Give it a moment to start.
        var timeoutAt = DateTimeOffset.Now.AddSeconds(8);
        while (DateTimeOffset.Now < timeoutAt && !ct.IsCancellationRequested)
        {
            if (await IsServerRunningAsync(ct))
                return;
            await Task.Delay(300, ct);
        }
    }

    public async Task PullModelAsync(string model, CancellationToken ct = default)
    {
        model = (model ?? "").Trim();
        if (model.Length == 0) return;

        var psi = new ProcessStartInfo
        {
            FileName = "ollama",
            Arguments = $"pull {model}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var p = Process.Start(psi);
        if (p is null) return;
        await p.WaitForExitAsync(ct);
    }

    public async Task StopModelAsync(string model, CancellationToken ct = default)
    {
        model = (model ?? "").Trim();
        if (model.Length == 0) return;

        var psi = new ProcessStartInfo
        {
            FileName = "ollama",
            Arguments = $"stop {model}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var p = Process.Start(psi);
        if (p is null) return;
        await p.WaitForExitAsync(ct);
    }

    public void Dispose()
    {
        try { _http.Dispose(); } catch { }

        // Only stop the server if we started it.
        if (_serveProcess is not null)
        {
            try
            {
                if (!_serveProcess.HasExited)
                    _serveProcess.Kill(entireProcessTree: true);
            }
            catch { }
            finally
            {
                try { _serveProcess.Dispose(); } catch { }
                _serveProcess = null;
            }
        }
    }
}

