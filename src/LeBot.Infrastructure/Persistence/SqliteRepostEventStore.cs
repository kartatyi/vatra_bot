using LeBot.Application.Telemetry;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LeBot.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed <see cref="IRepostEventStore"/>. A singleton, so it creates a short-lived context per
/// write through <see cref="IDbContextFactory{TContext}"/> rather than capturing a scoped context — the
/// pattern for long-lived consumers. The dispatcher handles updates one at a time, so these writes don't
/// contend with each other; WAL (enabled at startup) keeps them from colliding with a dashboard reader.
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
}
