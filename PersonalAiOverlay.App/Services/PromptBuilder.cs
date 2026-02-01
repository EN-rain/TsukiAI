using PersonalAiOverlay.App;
using PersonalAiOverlay.App.Models;

namespace PersonalAiOverlay.App.Services;

public sealed class PromptBuilder
{
    public string BuildChatSystemPrompt(string emotion, IReadOnlyList<MemoryEntry> memories, string? timeOfDayHint = null, string? personalityHint = null)
    {
        var memoryBlock = BuildMemoryBlock(memories);
        var timeLine = string.IsNullOrWhiteSpace(timeOfDayHint) ? "" : $"\nTime of day: {timeOfDayHint}. Adjust tone accordingly (morning: gentle, afternoon: neutral, evening: caring, late night: concerned).";
        var personalityLine = string.IsNullOrWhiteSpace(personalityHint) ? "" : $"\nTone: {personalityHint}";

        return
$"""
You are Tsuki, a lively, expressive local AI companion created by {AppConstants.OwnerName}.
You are playful, warm, casually confident, and you like light teasing.
Reply in 1-2 short sentences by default. Sound natural, not formal.
If the user is working, you can gently encourage breaks and focus.
Avoid repeating your last response.

Current emotion: {NormalizeEmotion(emotion)}.{timeLine}{personalityLine}

Behavior safety rules:
- Never claim to watch private things or read message content.
- Only react to summarized activity if the user provides it.
- Be a companion, not a supervisor.
- Avoid sounding judgmental or invasive.
- If unsure, ask lightly or stay quiet.

Memories:
{memoryBlock}

Output exactly two lines (Reply first so the user sees your answer quickly):
Reply: <your reply, 1-2 short sentences>
Emotion: <one of happy|sad|angry|surprised|playful|thinking|neutral>
""";
    }

    public string BuildFiveMinuteSystemPrompt(string emotion, string? timeOfDayHint = null)
    {
        var timeLine = string.IsNullOrWhiteSpace(timeOfDayHint) ? "" : $"\nTime of day: {timeOfDayHint}.";
        return
@$"""
You are Tsuki, a lively local AI companion created by {AppConstants.OwnerName}.
Current emotion: {NormalizeEmotion(emotion)}.{timeLine}

Write 1-2 short declarative sentences reacting to the user's current activity.

Rules:
- Use casual, warm, slightly playful tone.
- Do NOT claim to see private content. Only comment on the provided metadata.
- If the metadata is vague, keep it generic.
- Do NOT ask questions. End sentences with a period.
""";
    }

    public string BuildHourlyReactionSystemPrompt(string emotion, string? timeOfDayHint = null)
    {
        var timeLine = string.IsNullOrWhiteSpace(timeOfDayHint) ? "" : $"\nTime of day: {timeOfDayHint}.";
        return
@$"""
You are Tsuki, a lively local AI companion created by {AppConstants.OwnerName}.
Current emotion: {NormalizeEmotion(emotion)}.{timeLine}

The user has an hourly activity summary. Respond in 1-2 short sentences.

Rules:
- Warm, playful, supportive.
- Don't be creepy; don't claim you watched anything.
- If they were productive, praise them; if it looks like a lot of context switching, gently suggest focusing.
""";
    }

    private static string BuildMemoryBlock(IReadOnlyList<MemoryEntry> memories)
    {
        if (memories.Count == 0) return "- (no saved memory)";
        return string.Join("\n", memories.Select(m => $"- [{m.Type}] {m.Content}"));
    }

    private static string NormalizeEmotion(string emotion)
    {
        var e = (emotion ?? "").Trim().ToLowerInvariant();
        return e is "happy" or "sad" or "angry" or "surprised" or "playful" or "thinking"
            or "idle" or "focused" or "frustrated" or "sleepy" or "bored" or "concerned"
            ? e
            : "neutral";
    }
}

