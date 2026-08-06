using System.Data.Common;
using LeBot.Application.Telemetry;

namespace LeBot.Dashboard;

/// <summary>
/// Reads one <see cref="DashboardSnapshot"/> from the journal for the requested window. Read-only: every
/// call is a fan-out of the store's query methods. A missing or unreadable database degrades to an empty
/// snapshot with a notice rather than a 500 — the dashboard is a diagnostic and must stay up even when the
/// bot hasn't written anything yet.
/// </summary>
internal static class DashboardSnapshotLoader
{
    private const int RecentFailuresLimit = 100;
    private const int TopHostsLimit = 8;
    private const int FailureRateMinVolume = 3;

    public static async Task<DashboardSnapshot> LoadAsync(
        IRepostEventStore store,
        TimeProvider clock,
        string databasePath,
        int days,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var windowDays = days < 0 ? 0 : days;
        var since = windowDays == 0 ? DateTimeOffset.MinValue : now - TimeSpan.FromDays(windowDays);

        if (!File.Exists(databasePath))
        {
            return DashboardSnapshot.Empty(now, windowDays,
                $"Journal database not found at {databasePath}. Is the bot running and writing telemetry?");
        }

        try
        {
            var stats = await store.GetStatsAsync(since, cancellationToken);
            var latency = await store.GetLatencyAsync(since, cancellationToken);
            var daily = await store.GetDailyOutcomesAsync(since, cancellationToken);
            var byVolume = await store.GetTopHostsByVolumeAsync(TopHostsLimit, since, cancellationToken);
            var byFailureRate = await store.GetTopHostsByFailureRateAsync(TopHostsLimit, FailureRateMinVolume, since, cancellationToken);
            var extractors = await store.GetExtractorStatsAsync(since, cancellationToken);
            var versions = await store.GetVersionStatsAsync(since, cancellationToken);
            var failures = await store.GetRecentFailuresAsync(RecentFailuresLimit, cancellationToken);

            return new DashboardSnapshot(
                now, windowDays, Notice: null,
                stats, latency, daily, byVolume, byFailureRate, extractors, versions, failures);
        }
        catch (DbException ex)
        {
            // Locked, corrupt, or schema-drifted DB: show the reason instead of failing the page.
            return DashboardSnapshot.Empty(now, windowDays, $"Could not read the journal: {ex.Message}");
        }
    }
}
