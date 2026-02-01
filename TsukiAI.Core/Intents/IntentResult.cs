using TsukiAI.Core.Assistant;

namespace TsukiAI.Core.Intents;

public sealed record IntentResult(bool Handled, AssistantResponse Response)
{
    public static IntentResult NotHandled() => new(false, AssistantResponse.FromAssistant(""));
    public static IntentResult HandledWith(string assistantText, string userText = "")
        => new(true, new AssistantResponse(userText ?? "", assistantText ?? "", DateTimeOffset.Now));
}

