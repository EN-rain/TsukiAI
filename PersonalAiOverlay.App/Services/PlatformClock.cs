using PersonalAiOverlay.Core.Platform;

namespace PersonalAiOverlay.App.Services;

public sealed class PlatformClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
}

