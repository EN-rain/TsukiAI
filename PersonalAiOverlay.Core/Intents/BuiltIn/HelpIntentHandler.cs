using PersonalAiOverlay.Core.Assistant;

namespace PersonalAiOverlay.Core.Intents.BuiltIn;

public sealed class HelpIntentHandler : IIntentHandler
{
    public bool CanHandle(IntentContext context)
        => context.Text.Equals("/help", StringComparison.OrdinalIgnoreCase)
            || context.Text.Equals("help", StringComparison.OrdinalIgnoreCase);

    public IntentResult Handle(IntentContext context)
    {
        var help = """
Commands:
- /help                 Show this help
- time                  Show local time
- open <url>            Open a URL (e.g. open google.com)
- calc <expr>           Calculate (e.g. calc (12+8)/4)
- copy <text>           Copy text to clipboard
Tips:
- Ctrl+Alt+Space toggles the overlay
- Esc hides the overlay
""";

        return new IntentResult(true, new AssistantResponse(context.Text, help, DateTimeOffset.Now));
    }
}

