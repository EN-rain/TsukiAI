using System.Windows;
using PersonalAiOverlay.Core.Platform;

namespace PersonalAiOverlay.App.Services;

public sealed class WpfClipboard : IClipboard
{
    public void SetText(string text)
    {
        text ??= "";
        System.Windows.Clipboard.SetText(text);
    }
}

