using LeBot.Application.Telemetry;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LeBot.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed <see cref="IRepostEventStore"/>. A singleton, so it creates a short-lived context per
/// operation through <see cref="IDbContextFactory{TContext}"/> rather than capturing a scoped context —
/// the pattern for long-lived consumers. The dispatcher handles updates one at a time, so writes don't
/// contend with each other; WAL (enabled at startup) keeps them from colliding with a dashboard reader.
/// Reads run <see cref="EntityFrameworkQueryableExtensions.AsNoTracking{TEntity}"/> — they only project,
/// never mutate.
/// </summary>
internal sealed class SqliteRepostEventStore(
    IDbContextFactory<LeBotDbContext> contextFactory,
    ILogger<SqliteRepostEventStore> logger) : IRepostEventStore
{
    public async Task AppendAsync(RepostEvent repostEvent, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            db.RepostEvents.Add(repostEvent);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is DbUpdateException or SqliteException)
        {
            // Telemetry is auxiliary: a write failure (locked file, full disk, schema drift) must never
            // break the repost the user actually asked for. Log it and carry on.
            logger.LogWarning(ex, "Failed to journal repost event for {Host}", repostEvent.Host);
        }
    }

    public async Task<RepostStatsSnapshot> GetStatsAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var window = db.RepostEvents.AsNoTracking().Where(e => e.OccurredAt >= since);

        // One GROUP BY over a real column (Outcome maps to text); assemble the snapshot from the <=5 rows.
        var byOutcome = await window
            .GroupBy(e => e.Outcome)
            .Select(g => new { Outcome = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        int CountOf(RepostOutcome outcome) =>
            byOutcome.FirstOrDefault(row => row.Outcome == outcome)?.Count ?? 0;

        var distinctChats = await window
            .Select(e => e.ChatHash)
            .Distinct()
            .CountAsync(cancellationToken);

        // Nullable projection so MIN/MAX over an empty window return null instead of throwing.
        var firstAt = await window.MinAsync(e => (DateTimeOffset?)e.OccurredAt, cancellationToken);
        var lastAt = await window.MaxAsync(e => (DateTimeOffset?)e.OccurredAt, cancellationToken);

        return new RepostStatsSnapshot(
            TotalProcessed: byOutcome.Sum(row => row.Count),
            MediaReposts: CountOf(RepostOutcome.MediaRepost),
            TextFallbacks: CountOf(RepostOutcome.TextFallback),
            Failures: CountOf(RepostOutcome.Failure),
            NothingExtracted: CountOf(RepostOutcome.NothingExtracted),
            NoExtractor: CountOf(RepostOutcome.NoExtractor),
            DistinctChats: distinctChats,
            FirstEventAt: firstAt,
            LastEventAt: lastAt);
    }

    public async Task<IReadOnlyList<RecentFailure>> GetRecentFailuresAsync(int limit, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.RepostEvents.AsNoTracking()
            .Where(e => e.Outcome == RepostOutcome.Failure)
            .OrderByDescending(e => e.OccurredAt)
            .Take(limit)
            .Select(e => new RecentFailure(e.OccurredAt, e.Host, e.Url, e.ErrorVariant, e.ErrorReason, e.Extractor))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<HostStat>> GetTopHostsByVolumeAsync(int limit, DateTimeOffset since, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var hosts = await QueryHostStatsAsync(db, since, cancellationToken);
        return hosts
            .OrderByDescending(h => h.Total)
            .ThenBy(h => h.Host, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    public async Task<IReadOnlyList<HostStat>> GetTopHostsByFailureRateAsync(int limit, int minVolume, DateTimeOffset since, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var hosts = await QueryHostStatsAsync(db, since, cancellationToken);
        return hosts
            .Where(h => h.Total >= minVolume)
            .OrderByDescending(h => h.FailureRate)
            .ThenByDescending(h => h.Total)
            .ThenBy(h => h.Host, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    public async Task<IReadOnlyList<ExtractorStat>> GetExtractorStatsAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.RepostEvents.AsNoTracking()
            .Where(e => e.OccurredAt >= since && e.Extractor != null)
            .GroupBy(e => e.Extractor!)
            .Select(g => new
            {
                Extractor = g.Key,
                Total = g.Count(),
                Successes = g.Sum(e => e.Outcome == RepostOutcome.MediaRepost || e.Outcome == RepostOutcome.TextFallback ? 1 : 0),
                Failures = g.Sum(e => e.Outcome == RepostOutcome.Failure ? 1 : 0),
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new ExtractorStat(r.Extractor, r.Total, r.Successes, r.Failures))
            .OrderByDescending(s => s.Total)
            .ThenBy(s => s.Extractor, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<LatencySummary> GetLatencyAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var window = db.RepostEvents.AsNoTracking().Where(e => e.OccurredAt >= since);

        var count = await window.CountAsync(cancellationToken);
        if (count == 0)
        {
            return LatencySummary.Empty;
        }

        var average = await window.AverageAsync(e => (double)e.ElapsedMs, cancellationToken);

        // p95 by rank: the smallest value with at least 95% of the sample at or below it. Computed
        // server-side via OFFSET so we never pull the whole column into memory.
        var rank = Math.Clamp((int)Math.Ceiling(count * 0.95) - 1, 0, count - 1);
        var p95 = await window
            .OrderBy(e => e.ElapsedMs)
            .Skip(rank)
            .Select(e => e.ElapsedMs)
            .FirstAsync(cancellationToken);

        return new LatencySummary(count, average, p95);
    }

    /// <summary>
    /// One GROUP BY host over the window, materialised into <see cref="HostStat"/>s. Host cardinality on a
    /// personal bot is tiny (a handful of platforms), so the two "top hosts" reads sort and cap in memory
    /// off this rather than pushing two different ORDER BYs — including the failure-rate ratio — into SQL.
    /// </summary>
    private static async Task<List<HostStat>> QueryHostStatsAsync(LeBotDbContext db, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var rows = await db.RepostEvents.AsNoTracking()
            .Where(e => e.OccurredAt >= since)
            .GroupBy(e => e.Host)
            .Select(g => new
            {
                Host = g.Key,
                Total = g.Count(),
                Failures = g.Sum(e => e.Outcome == RepostOutcome.Failure ? 1 : 0),
            })
            .ToListAsync(cancellationToken);

        return rows.Select(r => new HostStat(r.Host, r.Total, r.Failures)).ToList();
    }
}
