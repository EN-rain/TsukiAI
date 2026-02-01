using System.Windows;
using System.Windows.Interop;
using PersonalAiOverlay.App.Interop;

namespace PersonalAiOverlay.App.Services;

public sealed class GlobalHotkeyService : IDisposable
{
    private const int HotkeyId = 1;
    private readonly HwndSource _source;
    private bool _registered;

    public event Action? Pressed;

    public GlobalHotkeyService(WindowInteropHelper helper)
    {
        if (helper.Handle == nint.Zero)
            throw new InvalidOperationException("Window handle not created yet.");

        _source = HwndSource.FromHwnd(helper.Handle)
            ?? throw new InvalidOperationException("Failed to create HwndSource.");

        _source.AddHook(WndProc);
    }

    public void RegisterCtrlAltSpace()
    {
        // VK_SPACE = 0x20
        _registered = NativeMethods.RegisterHotKey(
            _source.Handle,
            HotkeyId,
            NativeMethods.HotkeyModifiers.Control | NativeMethods.HotkeyModifiers.Alt | NativeMethods.HotkeyModifiers.NoRepeat,
            0x20
        );

        if (!_registered)
            throw new InvalidOperationException("Failed to register global hotkey (Ctrl+Alt+Space).");
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke();
        }

        return nint.Zero;
    }

    public void Dispose()
    {
        try
        {
            if (_registered)
                NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
        }
        catch
        {
            // ignore
        }

        try { _source.RemoveHook(WndProc); } catch { }
    }
}

