namespace TsukiAI.Core.Intents;

public interface IIntentHandler
{
    bool CanHandle(IntentContext context);
    IntentResult Handle(IntentContext context);
}

