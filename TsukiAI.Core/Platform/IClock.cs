namespace TsukiAI.Core.Platform;

public interface IClock
{
    DateTimeOffset Now { get; }
}

