using LeBot.Application.Ports;
using LeBot.Application.Telemetry;
using LeBot.Infrastructure.MediaCache;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LeBot.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed <see cref="ISeenLinkStore"/>. Links are keyed exactly as the media cache keys them
/// (<see cref="MediaCacheKey"/>), so "the same link" means the same thing in both places.
/// </summary>
internal sealed class SqliteSeenLinkStore(
    IDbContextFactory<LeBotDbContext> contextFactory,
    TimeProvider timeProvider,
    ILogger<SqliteSeenLinkStore> logger) : ISeenLinkStore
{
    public async Task<bool> MarkSeenAsync(long chatId, Uri url, CancellationToken cancellationToken)
    {
        var chatHash = ChatHasher.Of(chatId);
        var linkKey = MediaCacheKey.For(url).DirectoryName;
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            if (await db.SeenLinks.AnyAsync(s => s.ChatHash == chatHash && s.LinkKey == linkKey, cancellationToken))
            {
                return true;
            }

            db.SeenLinks.Add(new SeenLinkRow { ChatHash = chatHash, LinkKey = linkKey, FirstSeenAt = timeProvider.GetUtcNow() });
            await db.SaveChangesAsync(cancellationToken);
            return false;
        }
        catch (Exception ex) when (ex is DbUpdateException or SqliteException)
        {
            logger.LogWarning(ex, "Could not check whether chat {ChatHash} has seen a link", chatHash);
            return false;
        }
    }
}
