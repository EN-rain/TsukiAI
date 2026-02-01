namespace PersonalAiOverlay.Core.Assistant;

public sealed record AssistantResponse(
    string UserText,
    string AssistantText,
    DateTimeOffset Timestamp
)
{
    public static AssistantResponse FromAssistant(string assistantText, string userText = "")
        => new(userText ?? "", assistantText ?? "", DateTimeOffset.Now);
}

