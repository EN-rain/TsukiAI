namespace PersonalAiOverlay.App.Models;

public sealed record HourlySummary(
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    string SummaryText
);

