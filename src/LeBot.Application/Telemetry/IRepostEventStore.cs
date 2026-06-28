namespace LeBot.Application.Telemetry;

/// <summary>
/// Durable store for <see cref="RepostEvent"/>s — the persistence port behind the dashboard. It both
/// records events (append-only) and answers the rollup queries the dashboard surfaces (Telegram commands
/// and the local HTML view). The Infrastructure adapter owns the database; this layer only knows the
/// shape of the questions it can ask.
/// </summary>
/// <remarks>
/// The windowed reads take a <c>since</c> instant and count every event with
/// <see cref="RepostEvent.OccurredAt"/> at or after it; pass <see cref="DateTimeOffset.MinValue"/> for
/// all-time. Reads run untracked and never mutate state.
/// </remarks>
public interface IRepostEventStore
{
    /// <summary>
    /// Persists one event. <b>Best-effort:</b> the adapter logs and swallows storage failures rather
    /// than throwing, because dropping a telemetry row must never break the repost the user actually
    /// asked for. Honour <paramref name="cancellationToken"/> for shutdown.
    /// </summary>
    Task AppendAsync(RepostEvent repostEvent, CancellationToken cancellationToken);

    /// <summary>
    /// Rolls up every outcome in the window into a single <see cref="RepostStatsSnapshot"/> — totals per
    /// outcome, overall success rate, distinct chats, and the window's first/last event.
    /// </summary>
    Task<RepostStatsSnapshot> GetStatsAsync(DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>
    /// The most recent hard failures, newest first, capped at <paramref name="limit"/> — the broken links
    /// the <c>/failures</c> command and the dashboard table show.
    /// </summary>
    Task<IReadOnlyList<RecentFailure>> GetRecentFailuresAsync(int limit, CancellationToken cancellationToken);

    /// <summary>
    /// The <paramref name="limit"/> busiest hosts in the window, most events first.
    /// </summary>
    Task<IReadOnlyList<HostStat>> GetTopHostsByVolumeAsync(int limit, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>
    /// The <paramref name="limit"/> hosts with the highest failure rate in the window, considering only
    /// those with at least <paramref name="minVolume"/> events so a single 1-of-1 failure doesn't top the
    /// chart.
    /// </summary>
    Task<IReadOnlyList<HostStat>> GetTopHostsByFailureRateAsync(int limit, int minVolume, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>
    /// Per-extractor success/failure tallies in the window, busiest first. Rows with no extractor are
    /// excluded (see <see cref="ExtractorStat"/>).
    /// </summary>
    Task<IReadOnlyList<ExtractorStat>> GetExtractorStatsAsync(DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>
    /// Average and 95th-percentile elapsed time over the window; <see cref="LatencySummary.Empty"/> when
    /// the window has no events.
    /// </summary>
    Task<LatencySummary> GetLatencyAsync(DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>
    /// Per-UTC-day volume and success/failure split over the window, oldest day first — the HTML
    /// dashboard's "outcomes over time" chart.
    /// </summary>
    Task<IReadOnlyList<DailyOutcomeCount>> GetDailyOutcomesAsync(DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>
    /// Per-build success/failure tallies over the window, ordered by when each build first appeared
    /// (release order) — the dashboard's "regressions by version" view.
    /// </summary>
    Task<IReadOnlyList<VersionStat>> GetVersionStatsAsync(DateTimeOffset since, CancellationToken cancellationToken);
}
