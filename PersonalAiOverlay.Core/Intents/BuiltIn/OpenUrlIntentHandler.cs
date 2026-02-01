using PersonalAiOverlay.Core.Assistant;
using PersonalAiOverlay.Core.Platform;

namespace PersonalAiOverlay.Core.Intents.BuiltIn;

public sealed class OpenUrlIntentHandler(IUrlOpener opener) : IIntentHandler
{
    public bool CanHandle(IntentContext context)
        => context.Text.StartsWith("open ", StringComparison.OrdinalIgnoreCase);

    public IntentResult Handle(IntentContext context)
    {
        var target = context.Text["open ".Length..].Trim();
        if (string.IsNullOrWhiteSpace(target))
            return new IntentResult(true, AssistantResponse.FromAssistant("Usage: open <url>"));

        opener.Open(target);
        return new IntentResult(true, new AssistantResponse(context.Text, $"Opening: {target}", DateTimeOffset.Now));
    }
}

