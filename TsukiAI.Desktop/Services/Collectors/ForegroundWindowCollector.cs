using System.Diagnostics;
using System.Text;
using TsukiAI.Desktop.Interop;

namespace TsukiAI.Desktop.Services.Collectors;

public sealed class ForegroundWindowCollector
{
    public (string ProcessName, string WindowTitle) GetActiveProcessAndTitle()
    {
        var hwnd = ActivityNativeMethods.GetForegroundWindow();
        if (hwnd == nint.Zero)
            return ("", "");

        var title = GetWindowTitle(hwnd);
        var process = GetProcessName(hwnd);
        return (process, title);
    }

    private static string GetWindowTitle(nint hwnd)
    {
        var len = ActivityNativeMethods.GetWindowTextLengthW(hwnd);
        if (len <= 0) return "";

        var sb = new StringBuilder(len + 1);
        ActivityNativeMethods.GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString().Trim();
    }

    private static string GetProcessName(nint hwnd)
    {
        try
        {
            ActivityNativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return "";

            using var p = Process.GetProcessById((int)pid);
            return (p.ProcessName ?? "").Trim();
        }
        catch
        {
            return "";
        }
    }
}

