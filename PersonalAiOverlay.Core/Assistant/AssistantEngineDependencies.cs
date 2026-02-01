using PersonalAiOverlay.Core.Platform;

namespace PersonalAiOverlay.Core.Assistant;

public sealed record AssistantEngineDependencies(
    IClock Clock,
    IUrlOpener UrlOpener,
    IClipboard Clipboard
);

