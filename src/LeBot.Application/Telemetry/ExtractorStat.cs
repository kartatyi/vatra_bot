namespace LeBot.Application.Telemetry;

/// <summary>
/// Per-extractor rollup behind the dashboard's "which extractor is carrying its weight" breakdown — one
/// row per extractor in the window. Rows with no extractor (the <see cref="RepostOutcome.NoExtractor"/>
/// outcome) are excluded, since there is no extractor to attribute them to.
/// </summary>
/// <param name="Extractor">The extractor's type name (e.g. <c>YtDlpPlatformExtractor</c>).</param>
/// <param name="Total">Every journalled outcome this extractor produced in the window.</param>
/// <param name="Successes">How many were a media or text repost.</param>
/// <param name="Failures">How many were hard failures.</param>
public sealed record ExtractorStat(string Extractor, int Total, int Successes, int Failures)
{
    /// <summary>Fraction of this extractor's attempts that succeeded; 0 when it had no events.</summary>
    public double SuccessRate => Total == 0 ? 0d : (double)Successes / Total;
}
