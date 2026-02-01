using System.Diagnostics;
using PersonalAiOverlay.Core.Platform;

namespace PersonalAiOverlay.App.Services;

public sealed class UrlOpener : IUrlOpener
{
    public void Open(string urlOrHost)
    {
        urlOrHost ??= "";
        urlOrHost = urlOrHost.Trim();
        if (urlOrHost.Length == 0) return;

        var url = urlOrHost.Contains("://", StringComparison.OrdinalIgnoreCase)
            ? urlOrHost
            : $"https://{urlOrHost}";

        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}

