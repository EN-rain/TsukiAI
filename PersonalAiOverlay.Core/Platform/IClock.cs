namespace PersonalAiOverlay.Core.Platform;

public interface IClock
{
    DateTimeOffset Now { get; }
}

