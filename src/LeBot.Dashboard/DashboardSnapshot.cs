using LeBot.Application.Telemetry;

namespace LeBot.Dashboard;

/// <summary>
/// Everything the single-page UI needs in one payload, serialised to JSON at <c>/api/data</c>. It just
/// bundles the Application read-side DTOs for the window the operator picked; no logic of its own beyond
/// the empty/notice case (database missing or unreadable).
/// </summary>
/// <param name="GeneratedAtUtc">When this snapshot was assembled.</param>
/// <param name="WindowDays">The look-back window in days; 0 means all-time.</param>
/// <param name="Notice">A human message when the journal couldn't be read (else null).</param>
internal sealed record DashboardSnapshot(
    DateTimeOffset GeneratedAtUtc,
    int WindowDays,
    string? Notice,
    RepostStatsSnapshot Stats,
    LatencySummary Latency,
    IReadOnlyList<DailyOutcomeCount> Daily,
    IReadOnlyList<HostStat> TopByVolume,
    IReadOnlyList<HostStat> TopByFailureRate,
    IReadOnlyList<ExtractorStat> Extractors,
    IReadOnlyList<VersionStat> Versions,
    IReadOnlyList<RecentFailure> RecentFailures)
{
    /// <summary>A zeroed snapshot carrying a <paramref name="notice"/>, for the "no data to show" cases.</summary>
    public static DashboardSnapshot Empty(DateTimeOffset now, int windowDays, string notice) =>
        new(now, windowDays, notice, RepostStatsSnapshot.Empty, LatencySummary.Empty,
            [], [], [], [], [], []);
}
