using System.Windows;
using TsukiAI.Core.Platform;

namespace TsukiAI.Desktop.Services;

public sealed class WpfClipboard : IClipboard
{
    public void SetText(string text)
    {
        text ??= "";
        System.Windows.Clipboard.SetText(text);
    }
}

