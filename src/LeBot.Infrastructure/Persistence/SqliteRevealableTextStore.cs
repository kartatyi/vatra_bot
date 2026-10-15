using LeBot.Application.Ports;
using LeBot.Application.Telemetry;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LeBot.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed <see cref="IRevealableTextStore"/>. Entries live <see cref="Lifetime"/>: long enough for
/// anyone scrolling back through a busy chat, short enough that the table never grows past a week of
/// reposts. Each save also sweeps what has expired, so no separate cleanup job is needed.
/// </summary>
internal sealed class SqliteRevealableTextStore(
    IDbContextFactory<LeBotDbContext> contextFactory,
    TimeProvider timeProvider,
    ILogger<SqliteRevealableTextStore> logger) : IRevealableTextStore
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    public async Task SaveAsync(long chatId, int messageId, string text, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var expiredBefore = now - Lifetime;
            await db.RevealableTexts.Where(r => r.CreatedAt < expiredBefore).ExecuteDeleteAsync(cancellationToken);

            db.RevealableTexts.Add(new RevealableTextRow
            {
                ChatHash = ChatHasher.Of(chatId),
                MessageId = messageId,
                Text = text,
                CreatedAt = now,
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is DbUpdateException or SqliteException)
        {
            // The repost already went out; losing this only means its button will answer "no longer available".
            logger.LogWarning(ex, "Could not keep the held-back text of message {MessageId}", messageId);
        }
    }

    public async Task<string?> TakeAsync(long chatId, int messageId, CancellationToken cancellationToken)
    {
        var chatHash = ChatHasher.Of(chatId);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.RevealableTexts
            .FirstOrDefaultAsync(r => r.ChatHash == chatHash && r.MessageId == messageId, cancellationToken);
        if (row is null)
        {
            return null;
        }

        db.RevealableTexts.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
        return row.CreatedAt + Lifetime > timeProvider.GetUtcNow() ? row.Text : null;
    }
}
