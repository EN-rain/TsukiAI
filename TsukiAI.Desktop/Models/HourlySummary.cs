namespace TsukiAI.Desktop.Models;

public sealed record HourlySummary(
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    string SummaryText
);

