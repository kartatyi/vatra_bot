using LeBot.Application.Ports;
using LeBot.Application.Telemetry;
using LeBot.Domain.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LeBot.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed <see cref="IChatSettingsStore"/>, in the same database file as the repost journal. A
/// singleton that opens a short-lived context per call, like <see cref="SqliteRepostEventStore"/>.
/// </summary>
internal sealed class SqliteChatSettingsStore(
    IDbContextFactory<LeBotDbContext> contextFactory,
    TimeProvider timeProvider,
    ILogger<SqliteChatSettingsStore> logger) : IChatSettingsStore
{
    public async Task<ChatSettings> GetAsync(long chatId, CancellationToken cancellationToken)
    {
        var chatHash = ChatHasher.Of(chatId);
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var row = await db.ChatSettings.AsNoTracking()
                .FirstOrDefaultAsync(r => r.ChatHash == chatHash, cancellationToken);
            return row is null ? ChatSettings.Default : new ChatSettings(row.VideoCaption, row.PostCaption, row.OfferFullText, row.ShrugOnFailure, row.WeeklyDigest);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
        {
            // A locked or broken file must not cost the chat its repost: fall back to the defaults.
            logger.LogWarning(ex, "Could not read settings for chat {ChatHash}; using defaults", chatHash);
            return ChatSettings.Default;
        }
    }

    public async Task SaveAsync(long chatId, ChatSettings settings, CancellationToken cancellationToken)
    {
        var chatHash = ChatHasher.Of(chatId);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.ChatSettings.FirstOrDefaultAsync(r => r.ChatHash == chatHash, cancellationToken);
        if (row is null)
        {
            row = new ChatSettingsRow { ChatHash = chatHash };
            db.ChatSettings.Add(row);
        }

        row.VideoCaption = settings.VideoCaption;
        row.PostCaption = settings.PostCaption;
        row.OfferFullText = settings.OfferFullText;
        row.ShrugOnFailure = settings.ShrugOnFailure;
        row.WeeklyDigest = settings.WeeklyDigest;
        row.ChatId = settings.WeeklyDigest ? chatId : null;
        row.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<long>> GetDigestChatIdsAsync(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.ChatSettings.AsNoTracking()
            .Where(r => r.WeeklyDigest && r.ChatId != null)
            .Select(r => r.ChatId ?? 0) // "?? 0" never fires: the filter keeps rows with an id
            .ToListAsync(cancellationToken);
    }
}
