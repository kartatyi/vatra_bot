using System.Text.Json;
using LeBot.Application.Caching;
using LeBot.Application.Ports;
using LeBot.Domain.Media;
using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.Persistence;
using LeBot.Infrastructure.Telegram;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.MediaCache;

/// <summary>
/// An <see cref="IMediaCache"/> that keeps no bytes: a post's media goes up to the storage chat once
/// (<see cref="TelegramMediaVault"/>), and the cache remembers the Telegram file ids beside the post's text.
/// A repeat link is then sent by file id — no download, no upload, nothing on disk — and the fresh repost
/// itself is sent the same way, straight after the upload, so a post is only ever uploaded once.
/// </summary>
/// <remarks>
/// Keyed by <see cref="MediaCacheKey"/>, like the disk store, so the same link means the same thing in both.
/// Entries live <see cref="MediaCacheOptions.TtlHours"/> from the moment they're stored: the file ids would
/// outlive that, but the post's text could go stale.
/// </remarks>
internal sealed class TelegramMediaCache(
    TelegramMediaVault vault,
    IDbContextFactory<LeBotDbContext> contextFactory,
    IOptions<MediaCacheOptions> options,
    TimeProvider timeProvider,
    ILogger<TelegramMediaCache> logger) : IMediaCache
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<CachedRepost?> TryGetAsync(Uri url, CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            return null;
        }

        var key = MediaCacheKey.For(url);
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var row = await db.StoredPosts.AsNoTracking().FirstOrDefaultAsync(p => p.LinkKey == key.DirectoryName, cancellationToken);
            if (row is null || row.StoredAt + options.Value.Ttl <= timeProvider.GetUtcNow())
            {
                return null;
            }

            var document = JsonSerializer.Deserialize<StoredPostDocument>(row.Document, Json);
            if (document is null || document.NormalizedUrl != key.NormalizedUrl)
            {
                return null; // a digest collision, or a row nobody can read — either way, a miss
            }

            var items = document.Items
                .Select(i => new MediaItem(string.Empty, i.Kind, i.MimeType, i.SizeBytes, i.DurationSeconds) { TelegramFileId = i.FileId })
                .ToList();
            var payload = new MediaPayload(document.SourceUrl, document.Title, document.Author, items, document.Description, RetainFiles: true);
            return new CachedRepost(payload, document.Extractor);
        }
        catch (Exception ex) when (ex is SqliteException or JsonException)
        {
            logger.LogWarning(ex, "Could not read the stored post for {Url}", url);
            return null;
        }
    }

    public async Task<MediaPayload> SaveAsync(MediaPayload payload, string extractor, CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled || payload.RetainFiles)
        {
            return payload; // off, or the payload came out of a store in the first place
        }

        var toSend = payload;
        if (payload.HasMedia)
        {
            if (await vault.StoreAsync(payload, cancellationToken) is not { } stored)
            {
                return payload; // no storage chat or a failed upload: the caller sends its own files, uncached
            }

            DeleteDownloads(payload);
            toSend = payload with { Items = stored, RetainFiles = true };
        }

        await WriteAsync(toSend, extractor, cancellationToken);
        return toSend;
    }

    private async Task WriteAsync(MediaPayload payload, string extractor, CancellationToken cancellationToken)
    {
        var key = MediaCacheKey.For(payload.SourceUrl);
        var document = new StoredPostDocument(
            key.NormalizedUrl,
            payload.SourceUrl,
            extractor,
            payload.Title,
            payload.Author,
            payload.Description,
            payload.Items
                .Where(i => i.TelegramFileId is not null)
                .Select(i => new StoredMediaItem(i.TelegramFileId!, i.Kind, i.MimeType, i.SizeBytes, i.DurationSeconds)) // non-null: filtered above
                .ToList());
        var now = timeProvider.GetUtcNow();

        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var expiredBefore = now - options.Value.Ttl;
            await db.StoredPosts.Where(p => p.StoredAt < expiredBefore || p.LinkKey == key.DirectoryName).ExecuteDeleteAsync(cancellationToken);
            db.StoredPosts.Add(new StoredPostRow { LinkKey = key.DirectoryName, Document = JsonSerializer.Serialize(document, Json), StoredAt = now });
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is DbUpdateException or SqliteException)
        {
            // The media is already in the storage chat and the repost goes out regardless; only the next
            // repeat of this link will have to fetch it again.
            logger.LogWarning(ex, "Could not remember the stored post for {Url}", payload.SourceUrl);
        }
    }

    /// <summary>Once the media is in Telegram, the download has done its job — this is the "nothing on disk" part.</summary>
    private void DeleteDownloads(MediaPayload payload)
    {
        foreach (var item in payload.Items.Where(i => i.FilePath.Length > 0))
        {
            try
            {
                File.Delete(item.FilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Could not delete {Path}", item.FilePath);
            }
        }
    }
}
