namespace LeBot.Application.Telemetry;

/// <summary>
/// Per-platform rollup behind "top platforms by volume" and "by failure rate" — one row per source host
/// in the window.
/// </summary>
/// <param name="Host">The platform host (e.g. <c>tiktok.com</c>).</param>
/// <param name="Total">Every journalled outcome for this host in the window.</param>
/// <param name="Failures">How many of those were hard failures.</param>
public sealed record HostStat(string Host, int Total, int Failures)
{
    /// <summary>Fraction of this host's URLs that failed hard; 0 when it had no events.</summary>
    public double FailureRate => Total == 0 ? 0d : (double)Failures / Total;
}
