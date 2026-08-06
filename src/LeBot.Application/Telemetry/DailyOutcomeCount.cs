namespace LeBot.Application.Telemetry;

/// <summary>
/// One day's worth of outcomes, for the dashboard's "outcomes over time" chart — volume and the
/// success/failure split, bucketed by UTC calendar day.
/// </summary>
/// <param name="Date">The UTC day these counts fall on.</param>
/// <param name="Total">Every journalled outcome on that day.</param>
/// <param name="Successes">Media or text reposts that day.</param>
/// <param name="Failures">Hard failures that day.</param>
public sealed record DailyOutcomeCount(DateOnly Date, int Total, int Successes, int Failures);
