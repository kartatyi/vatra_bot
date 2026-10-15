using LeBot.Application.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace LeBot.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed <see cref="IPersonStatsStore"/> over the repost journal and the link sightings. Every
/// query drops the people who opted out of the chat's ranking, in SQL, so nothing of theirs is even read.
/// </summary>
internal sealed class SqlitePersonStatsStore(IDbContextFactory<LeBotDbContext> contextFactory) : IPersonStatsStore
{
    public async Task<IReadOnlyList<SenderMoment>> GetRepostMomentsAsync(string chatHash, DateTimeOffset since, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var optedOut = OptedOut(db, chatHash);
        return await db.RepostEvents.AsNoTracking()
            .Where(e => e.ChatHash == chatHash
                && e.OccurredAt >= since
                && e.SenderId != null
                && (e.Outcome == RepostOutcome.MediaRepost || e.Outcome == RepostOutcome.TextFallback)
                && !optedOut.Contains(e.SenderId ?? 0))
            // "?? 0" never fires — null senders are filtered out above — but keeps the projection non-nullable.
            .Select(e => new SenderMoment(e.SenderId ?? 0, e.OccurredAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SenderCount>> GetRepeatCountsAsync(string chatHash, DateTimeOffset since, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var optedOut = OptedOut(db, chatHash);
        return await db.LinkSightings.AsNoTracking()
            .Where(s => s.ChatHash == chatHash && s.IsRepeat && s.SeenAt >= since && s.SenderId != null && !optedOut.Contains(s.SenderId ?? 0))
            .GroupBy(s => s.SenderId ?? 0)
            .Select(g => new SenderCount(g.Key, g.Count()))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SenderCount>> GetTrendsetterCountsAsync(string chatHash, DateTimeOffset since, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var optedOut = OptedOut(db, chatHash);
        var sightings = db.LinkSightings.AsNoTracking().Where(s => s.ChatHash == chatHash);
        return await sightings
            .Where(first => !first.IsRepeat && first.SeenAt >= since && first.SenderId != null && !optedOut.Contains(first.SenderId ?? 0))
            .Where(first => sightings.Any(later =>
                later.LinkKey == first.LinkKey && later.IsRepeat && later.SenderId != null && later.SenderId != first.SenderId))
            .GroupBy(first => first.SenderId ?? 0)
            .Select(g => new SenderCount(g.Key, g.Count()))
            .ToListAsync(cancellationToken);
    }

    private static IQueryable<long> OptedOut(LeBotDbContext db, string chatHash) =>
        db.StatsOptOuts.Where(o => o.ChatHash == chatHash).Select(o => o.UserId);
}
