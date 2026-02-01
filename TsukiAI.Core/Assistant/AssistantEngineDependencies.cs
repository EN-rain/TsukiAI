using TsukiAI.Core.Platform;

namespace TsukiAI.Core.Assistant;

public sealed record AssistantEngineDependencies(
    IClock Clock,
    IUrlOpener UrlOpener,
    IClipboard Clipboard
);

