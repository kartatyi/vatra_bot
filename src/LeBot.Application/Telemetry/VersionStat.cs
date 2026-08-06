namespace LeBot.Application.Telemetry;

/// <summary>
/// Per-build rollup behind the dashboard's "regressions by version" view: if a release's success rate
/// drops against the one before it, this is where it shows. One row per <see cref="RepostEvent.BotVersion"/>
/// in the window, ordered by when the build first appeared (release order).
/// </summary>
/// <param name="BotVersion">The build's version string (e.g. <c>1.4.0</c>).</param>
/// <param name="Total">Every journalled outcome this build produced in the window.</param>
/// <param name="Successes">How many were a media or text repost.</param>
/// <param name="Failures">How many were hard failures.</param>
public sealed record VersionStat(string BotVersion, int Total, int Successes, int Failures)
{
    /// <summary>Fraction of this build's attempts that succeeded; 0 when it had no events.</summary>
    public double SuccessRate => Total == 0 ? 0d : (double)Successes / Total;
}
