using LeBot.Application.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace LeBot.Infrastructure.Persistence;

/// <summary>SQLite-backed <see cref="IStatsOptOutStore"/>; the rows are read by <see cref="SqliteRepostEventStore.GetSenderHostCountsAsync"/>.</summary>
internal sealed class SqliteStatsOptOutStore(IDbContextFactory<LeBotDbContext> contextFactory) : IStatsOptOutStore
{
    public async Task SetOptedOutAsync(long chatId, long userId, bool optedOut, CancellationToken cancellationToken)
    {
        var chatHash = ChatHasher.Of(chatId);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.StatsOptOuts
            .FirstOrDefaultAsync(o => o.ChatHash == chatHash && o.UserId == userId, cancellationToken);

        switch (optedOut, existing)
        {
            case (true, null):
                db.StatsOptOuts.Add(new StatsOptOutRow { ChatHash = chatHash, UserId = userId });
                break;
            case (false, { } row):
                db.StatsOptOuts.Remove(row);
                break;
            default:
                return; // already in the requested state
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> IsOptedOutAsync(long chatId, long userId, CancellationToken cancellationToken)
    {
        var chatHash = ChatHasher.Of(chatId);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.StatsOptOuts.AnyAsync(o => o.ChatHash == chatHash && o.UserId == userId, cancellationToken);
    }

    public async Task<IReadOnlySet<long>> GetOptedOutAsync(long chatId, CancellationToken cancellationToken)
    {
        var chatHash = ChatHasher.Of(chatId);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var ids = await db.StatsOptOuts.Where(o => o.ChatHash == chatHash).Select(o => o.UserId).ToListAsync(cancellationToken);
        return ids.ToHashSet();
    }
}
