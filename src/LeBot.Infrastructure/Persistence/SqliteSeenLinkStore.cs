using LeBot.Application.Ports;
using LeBot.Application.Telemetry;
using LeBot.Infrastructure.MediaCache;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LeBot.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed <see cref="ISeenLinkStore"/>: every sighting of a link is a <see cref="LinkSightingRow"/>.
/// Links are keyed exactly as the media cache keys them (<see cref="MediaCacheKey"/>), so "the same link"
/// means the same thing in both places.
/// </summary>
internal sealed class SqliteSeenLinkStore(
    IDbContextFactory<LeBotDbContext> contextFactory,
    TimeProvider timeProvider,
    ILogger<SqliteSeenLinkStore> logger) : ISeenLinkStore
{
    public async Task<bool> MarkSeenAsync(long chatId, Uri url, long? senderId, CancellationToken cancellationToken)
    {
        var chatHash = ChatHasher.Of(chatId);
        var linkKey = MediaCacheKey.For(url).DirectoryName;
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var isRepeat = await db.LinkSightings.AnyAsync(s => s.ChatHash == chatHash && s.LinkKey == linkKey, cancellationToken);

            db.LinkSightings.Add(new LinkSightingRow
            {
                ChatHash = chatHash,
                LinkKey = linkKey,
                SenderId = senderId,
                SeenAt = timeProvider.GetUtcNow(),
                IsRepeat = isRepeat,
            });
            await db.SaveChangesAsync(cancellationToken);
            return isRepeat;
        }
        catch (Exception ex) when (ex is DbUpdateException or SqliteException)
        {
            logger.LogWarning(ex, "Could not check whether chat {ChatHash} has seen a link", chatHash);
            return false;
        }
    }
}
