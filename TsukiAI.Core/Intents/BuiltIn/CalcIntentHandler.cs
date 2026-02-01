using TsukiAI.Core.Assistant;
using TsukiAI.Core.Utilities;

namespace TsukiAI.Core.Intents.BuiltIn;

public sealed class CalcIntentHandler : IIntentHandler
{
    public bool CanHandle(IntentContext context)
        => context.Text.StartsWith("calc ", StringComparison.OrdinalIgnoreCase);

    public IntentResult Handle(IntentContext context)
    {
        var expr = context.Text["calc ".Length..].Trim();
        if (string.IsNullOrWhiteSpace(expr))
            return new IntentResult(true, AssistantResponse.FromAssistant("Usage: calc <expression>"));

        if (!SimpleCalculator.TryEval(expr, out var value, out var error))
            return new IntentResult(true, new AssistantResponse(context.Text, $"Calc error: {error}", DateTimeOffset.Now));

        return new IntentResult(true, new AssistantResponse(context.Text, $"{expr} = {value}", DateTimeOffset.Now));
    }
}

