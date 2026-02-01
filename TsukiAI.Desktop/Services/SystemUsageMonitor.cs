using System.Diagnostics;

namespace TsukiAI.Desktop.Services;

public sealed class SystemUsageMonitor
{
    private readonly Process _self = Process.GetCurrentProcess();
    private DateTimeOffset _lastAt = DateTimeOffset.UtcNow;
    private TimeSpan _lastCpu = Process.GetCurrentProcess().TotalProcessorTime;

    public (double CpuPercent, double MemoryMb) GetUsage()
    {
        _self.Refresh();

        var now = DateTimeOffset.UtcNow;
        var cpu = _self.TotalProcessorTime;

        var dt = (now - _lastAt).TotalMilliseconds;
        var dcpu = (cpu - _lastCpu).TotalMilliseconds;

        _lastAt = now;
        _lastCpu = cpu;

        var cpuPercent = 0.0;
        if (dt > 0)
        {
            // Normalize by CPU cores.
            cpuPercent = (dcpu / dt) * 100.0 / Math.Max(1, Environment.ProcessorCount);
            if (cpuPercent < 0) cpuPercent = 0;
        }

        var memMb = _self.WorkingSet64 / (1024.0 * 1024.0);
        return (cpuPercent, memMb);
    }
}

