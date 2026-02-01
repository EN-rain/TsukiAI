using TsukiAI.Core.Platform;

namespace TsukiAI.Desktop.Services;

public sealed class PlatformClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
}

