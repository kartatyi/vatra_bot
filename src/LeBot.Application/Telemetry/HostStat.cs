namespace LeBot.Application.Telemetry;

/// <summary>
/// Per-platform rollup behind "top platforms by volume", "by failure rate", and the dashboard's
/// per-platform success-rate chart — one row per source host in the window.
/// </summary>
/// <param name="Host">The platform host (e.g. <c>tiktok.com</c>).</param>
/// <param name="Total">Every journalled outcome for this host in the window.</param>
/// <param name="Successes">How many were a media or text repost.</param>
/// <param name="Failures">How many were hard failures.</param>
public sealed record HostStat(string Host, int Total, int Successes, int Failures)
{
    /// <summary>Fraction of this host's URLs that succeeded (media or text); 0 when it had no events.</summary>
    public double SuccessRate => Total == 0 ? 0d : (double)Successes / Total;

    /// <summary>Fraction of this host's URLs that failed hard; 0 when it had no events.</summary>
    public double FailureRate => Total == 0 ? 0d : (double)Failures / Total;
}
