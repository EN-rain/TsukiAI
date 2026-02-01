using TsukiAI.Core.Assistant;
using TsukiAI.Core.Platform;

namespace TsukiAI.Core.Intents.BuiltIn;

public sealed class TimeIntentHandler(IClock clock) : IIntentHandler
{
    public bool CanHandle(IntentContext context)
        => context.Text.Equals("time", StringComparison.OrdinalIgnoreCase)
            || context.Text.Equals("clock", StringComparison.OrdinalIgnoreCase);

    public IntentResult Handle(IntentContext context)
    {
        var now = clock.Now.ToLocalTime();
        var msg = $"Local time: {now:yyyy-MM-dd HH:mm:ss}";
        return new IntentResult(true, new AssistantResponse(context.Text, msg, DateTimeOffset.Now));
    }
}

