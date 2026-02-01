using PersonalAiOverlay.Core.Assistant;
using PersonalAiOverlay.Core.Platform;

namespace PersonalAiOverlay.Core.Intents.BuiltIn;

public sealed class ClipboardIntentHandler(IClipboard clipboard) : IIntentHandler
{
    public bool CanHandle(IntentContext context)
        => context.Text.StartsWith("copy ", StringComparison.OrdinalIgnoreCase);

    public IntentResult Handle(IntentContext context)
    {
        var text = context.Text["copy ".Length..];
        if (string.IsNullOrWhiteSpace(text))
            return new IntentResult(true, AssistantResponse.FromAssistant("Usage: copy <text>"));

        clipboard.SetText(text);
        return new IntentResult(true, new AssistantResponse(context.Text, "Copied to clipboard.", DateTimeOffset.Now));
    }
}

