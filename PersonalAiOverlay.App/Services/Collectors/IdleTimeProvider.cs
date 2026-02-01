using PersonalAiOverlay.App.Interop;

namespace PersonalAiOverlay.App.Services.Collectors;

public sealed class IdleTimeProvider
{
    public int GetIdleSeconds()
    {
        try
        {
            var lii = new ActivityNativeMethods.LASTINPUTINFO
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<ActivityNativeMethods.LASTINPUTINFO>()
            };

            if (!ActivityNativeMethods.GetLastInputInfo(ref lii))
                return 0;

            var tick = ActivityNativeMethods.GetTickCount();
            var idleMs = unchecked((int)(tick - lii.dwTime));
            if (idleMs < 0) idleMs = 0;
            return idleMs / 1000;
        }
        catch
        {
            return 0;
        }
    }
}

