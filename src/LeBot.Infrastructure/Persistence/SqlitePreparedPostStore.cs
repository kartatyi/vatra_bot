using LeBot.Application.Ports;
using LeBot.Application.UseCases.AnswerInlineQuery;
using LeBot.Infrastructure.MediaCache;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LeBot.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed <see cref="IPreparedPostStore"/>. A Telegram file id stays valid for as long as the file
/// lives in the storage chat, so entries live <see cref="Lifetime"/> — long enough that a popular link is
/// uploaded once, short enough that an edited post isn't served stale for ever.
/// </summary>
internal sealed class SqlitePreparedPostStore(
    IDbContextFactory<LeBotDbContext> contextFactory,
    TimeProvider timeProvider,
    ILogger<SqlitePreparedPostStore> logger) : IPreparedPostStore
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    public async Task<PreparedPost?> TryGetAsync(Uri url, CancellationToken cancellationToken)
    {
        var key = MediaCacheKey.For(url).DirectoryName;
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var row = await db.PreparedPosts.AsNoTracking().FirstOrDefaultAsync(p => p.LinkKey == key, cancellationToken);
            return row is not null && row.PreparedAt + Lifetime > timeProvider.GetUtcNow()
                ? new PreparedPost(row.Kind, row.FileId, row.Body)
                : null;
        }
        catch (SqliteException ex)
        {
            logger.LogWarning(ex, "Could not read a prepared inline post");
            return null;
        }
    }

    public async Task SaveAsync(Uri url, PreparedPost post, CancellationToken cancellationToken)
    {
        var key = MediaCacheKey.For(url).DirectoryName;
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            // Replace, don't merge: an expired row is being refreshed.
            await db.PreparedPosts.Where(p => p.LinkKey == key).ExecuteDeleteAsync(cancellationToken);
            db.PreparedPosts.Add(new PreparedPostRow
            {
                LinkKey = key,
                Kind = post.Kind,
                FileId = post.FileId,
                Body = post.Body,
                PreparedAt = timeProvider.GetUtcNow(),
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is DbUpdateException or SqliteException)
        {
            logger.LogWarning(ex, "Could not keep a prepared inline post");
        }
    }
}
