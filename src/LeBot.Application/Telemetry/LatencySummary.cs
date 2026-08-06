namespace LeBot.Application.Telemetry;

/// <summary>
/// A coarse latency picture over the window — the average and the 95th-percentile wall-clock time the bot
/// spent per URL (<see cref="RepostEvent.ElapsedMs"/>). Deliberately simple: two numbers and a sample
/// count, enough for the dashboard to flag "things got slow" without a histogram.
/// </summary>
/// <param name="SampleCount">How many events the figures are computed over.</param>
/// <param name="AverageMs">Mean elapsed milliseconds.</param>
/// <param name="P95Ms">The 95th-percentile elapsed milliseconds (the slow tail most users still hit).</param>
public sealed record LatencySummary(int SampleCount, double AverageMs, long P95Ms)
{
    /// <summary>The zero summary, for an empty window.</summary>
    public static LatencySummary Empty { get; } = new(0, 0d, 0L);
}
