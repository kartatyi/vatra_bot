using LeBot.Domain.Media;
using LeBot.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;

namespace LeBot.Infrastructure.Telegram;

/// <summary>
/// Uploads a post's media to the storage chat and reports the Telegram file ids it got back. A file id
/// can be sent to any chat afterwards with no upload at all, which is what lets the bot keep nothing on
/// disk. Several items go up as one album, so a carousel costs one round trip, not ten.
/// </summary>
internal sealed class TelegramMediaVault(
    ITelegramBotClient bot,
    IOptions<StorageOptions> options,
    ILogger<TelegramMediaVault> logger)
{
    /// <summary>Telegram's album ceiling; anything past it isn't reposted either.</summary>
    private const int MaxAlbumSize = 10;

    /// <summary>Whether a storage chat is configured at all.</summary>
    public bool IsConfigured => options.Value.ChatId is not null;

    /// <summary>
    /// The payload's items, each carrying its <see cref="MediaItem.TelegramFileId"/> and no local path; or
    /// <see langword="null"/> when there's no storage chat or the upload failed. Items already in Telegram
    /// are passed through. Never deletes the caller's files — they may still be needed if this fails.
    /// </summary>
    public async Task<IReadOnlyList<MediaItem>?> StoreAsync(MediaPayload payload, CancellationToken cancellationToken)
    {
        if (options.Value.ChatId is not { } storage || payload.Items.Count == 0)
        {
            return null;
        }

        var items = payload.Items.Take(MaxAlbumSize).ToList();
        if (items.All(item => item.TelegramFileId is not null))
        {
            return items;
        }

        try
        {
            var fileIds = items.Count == 1
                ? [await UploadOneAsync(storage, items[0], cancellationToken)]
                : await UploadAlbumAsync(storage, items, cancellationToken);

            if (fileIds.Count != items.Count || fileIds.Any(id => id is null))
            {
                logger.LogWarning("Storage chat returned {Got} file ids for {Sent} items of {Url}", fileIds.Count, items.Count, payload.SourceUrl);
                return null;
            }

            return items.Select((item, i) => item with { FilePath = string.Empty, TelegramFileId = fileIds[i] }).ToList();
        }
        catch (Exception ex) when (ex is RequestException or IOException)
        {
            logger.LogWarning(ex, "Could not put the media of {Url} into the storage chat", payload.SourceUrl);
            return null;
        }
    }

    private async Task<string?> UploadOneAsync(long storage, MediaItem item, CancellationToken cancellationToken)
    {
        if (item.TelegramFileId is { } existing)
        {
            return existing;
        }

        await using var stream = File.OpenRead(item.FilePath);
        var file = InputFile.FromStream(stream, Path.GetFileName(item.FilePath));
        var sent = item.Kind switch
        {
            MediaKind.Photo => await bot.SendPhoto(storage, file, cancellationToken: cancellationToken),
            MediaKind.Animation => await bot.SendAnimation(storage, file, cancellationToken: cancellationToken),
            MediaKind.Audio => await bot.SendAudio(storage, file, cancellationToken: cancellationToken),
            _ => await bot.SendVideo(storage, file, duration: item.DurationSeconds, supportsStreaming: true, cancellationToken: cancellationToken),
        };
        return FileIdOf(sent);
    }

    private async Task<IReadOnlyList<string?>> UploadAlbumAsync(long storage, List<MediaItem> items, CancellationToken cancellationToken)
    {
        var streams = new List<FileStream>();
        try
        {
            var album = new List<IAlbumInputMedia>(items.Count);
            foreach (var item in items)
            {
                InputFile file;
                if (item.TelegramFileId is { } existing)
                {
                    file = InputFile.FromFileId(existing);
                }
                else
                {
                    var stream = File.OpenRead(item.FilePath);
                    streams.Add(stream);
                    file = InputFile.FromStream(stream, Path.GetFileName(item.FilePath));
                }

                album.Add(item.Kind switch
                {
                    MediaKind.Photo => new InputMediaPhoto(file),
                    MediaKind.Video => new InputMediaVideo(file) { SupportsStreaming = true },
                    _ => new InputMediaDocument(file),
                });
            }

            var sent = await bot.SendMediaGroup(storage, album, cancellationToken: cancellationToken);
            return sent.Select(FileIdOf).ToList();
        }
        finally
        {
            foreach (var stream in streams)
            {
                await stream.DisposeAsync();
            }
        }
    }

    private static string? FileIdOf(Message message) =>
        message.Video?.FileId
        ?? message.Photo?.LastOrDefault()?.FileId
        ?? message.Animation?.FileId
        ?? message.Audio?.FileId
        ?? message.Document?.FileId;
}
