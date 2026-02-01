using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using TsukiAI.Desktop.Interop;
using TsukiAI.Desktop.Models;

namespace TsukiAI.Desktop.Services.Collectors;

public sealed class ScreenshotCapturer
{
    public string CapturePng(string outputPath, ScreenshotCaptureMode mode)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

        return mode switch
        {
            ScreenshotCaptureMode.ActiveWindow => CaptureActiveWindow(outputPath),
            _ => CaptureFullScreen(outputPath),
        };
    }

    /// <summary>
    /// Capture full screen and return as Bitmap.
    /// </summary>
    public Bitmap CaptureFullScreen()
    {
        var x = ActivityNativeMethods.GetSystemMetrics(ActivityNativeMethods.SM_XVIRTUALSCREEN);
        var y = ActivityNativeMethods.GetSystemMetrics(ActivityNativeMethods.SM_YVIRTUALSCREEN);
        var w = ActivityNativeMethods.GetSystemMetrics(ActivityNativeMethods.SM_CXVIRTUALSCREEN);
        var h = ActivityNativeMethods.GetSystemMetrics(ActivityNativeMethods.SM_CYVIRTUALSCREEN);

        if (w <= 0 || h <= 0)
            throw new InvalidOperationException("Virtual screen bounds are invalid.");

        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
        }

        return bmp;
    }

    private static string CaptureFullScreen(string outputPath)
    {
        var x = ActivityNativeMethods.GetSystemMetrics(ActivityNativeMethods.SM_XVIRTUALSCREEN);
        var y = ActivityNativeMethods.GetSystemMetrics(ActivityNativeMethods.SM_YVIRTUALSCREEN);
        var w = ActivityNativeMethods.GetSystemMetrics(ActivityNativeMethods.SM_CXVIRTUALSCREEN);
        var h = ActivityNativeMethods.GetSystemMetrics(ActivityNativeMethods.SM_CYVIRTUALSCREEN);

        if (w <= 0 || h <= 0)
            throw new InvalidOperationException("Virtual screen bounds are invalid.");

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
        }

        bmp.Save(outputPath, ImageFormat.Png);
        return outputPath;
    }

    private static string CaptureActiveWindow(string outputPath)
    {
        var hwnd = ActivityNativeMethods.GetForegroundWindow();
        if (hwnd == nint.Zero)
            return CaptureFullScreen(outputPath);

        if (!ActivityNativeMethods.GetWindowRect(hwnd, out var r))
            return CaptureFullScreen(outputPath);

        var w = Math.Max(1, r.Right - r.Left);
        var h = Math.Max(1, r.Bottom - r.Top);

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
        }

        bmp.Save(outputPath, ImageFormat.Png);
        return outputPath;
    }
}

