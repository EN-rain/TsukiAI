using PersonalAiOverlay.Core.Intents;
using PersonalAiOverlay.Core.Intents.BuiltIn;

namespace PersonalAiOverlay.Core.Assistant;

public sealed class AssistantEngine
{
    private readonly List<IIntentHandler> _handlers;

    public AssistantEngine(AssistantEngineDependencies deps)
    {
        if (deps is null) throw new ArgumentNullException(nameof(deps));

        _handlers =
        [
            new HelpIntentHandler(),
            new TimeIntentHandler(deps.Clock),
            new OpenUrlIntentHandler(deps.UrlOpener),
            new CalcIntentHandler(),
            new ClipboardIntentHandler(deps.Clipboard),
        ];
    }

    public AssistantResponse Handle(string userText)
    {
        if (TryHandle(userText, out var response))
            return response;

        return AssistantResponse.FromAssistant("I don't recognize that yet. Type `/help` to see what I can do.");
    }

    public bool TryHandle(string userText, out AssistantResponse response)
    {
        userText ??= string.Empty;

        var ctx = new IntentContext(userText.Trim());
        if (string.IsNullOrWhiteSpace(ctx.Text))
        {
            response = AssistantResponse.FromAssistant("Type `/help` for commands.");
            return true;
        }

        foreach (var h in _handlers)
        {
            if (!h.CanHandle(ctx))
                continue;

            var result = h.Handle(ctx);
            if (result.Handled)
            {
                response = result.Response;
                return true;
            }
        }

        response = AssistantResponse.FromAssistant("");
        return false;
    }
}

