using LeBot.Application.Ports;
using LeBot.Domain.Media;
using LeBot.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;

namespace LeBot.Infrastructure.Telegram;

/// <summary>
/// <see cref="IInlineMediaUploader"/> over the storage chat from <see cref="InlineOptions"/>. Only the
/// first item goes up: an inline result is a single file — Telegram has no inline albums.
/// </summary>
internal sealed class TelegramInlineMediaUploader(
    ITelegramBotClient bot,
    IOptions<InlineOptions> options,
    ILogger<TelegramInlineMediaUploader> logger) : IInlineMediaUploader
{
    public async Task<(MediaKind Kind, string FileId)?> UploadAsync(MediaPayload payload, CancellationToken cancellationToken)
    {
        try
        {
            if (options.Value.StorageChatId is not { } storage)
            {
                logger.LogWarning("Inline media can't be uploaded: Inline:StorageChatId is not set");
                return null;
            }

            var item = payload.Items[0];
            await using var stream = File.OpenRead(item.FilePath);
            var file = InputFile.FromStream(stream, Path.GetFileName(item.FilePath));

            var fileId = item.Kind switch
            {
                MediaKind.Video => (await bot.SendVideo(storage, file, duration: item.DurationSeconds, supportsStreaming: true, cancellationToken: cancellationToken)).Video?.FileId,
                MediaKind.Photo => (await bot.SendPhoto(storage, file, cancellationToken: cancellationToken)).Photo?.LastOrDefault()?.FileId,
                MediaKind.Animation => (await bot.SendAnimation(storage, file, cancellationToken: cancellationToken)).Animation?.FileId,
                MediaKind.Audio => (await bot.SendAudio(storage, file, cancellationToken: cancellationToken)).Audio?.FileId,
                _ => null,
            };

            return fileId is null ? null : (item.Kind, fileId);
        }
        catch (RequestException ex)
        {
            logger.LogWarning(ex, "Uploading inline media for {Url} failed", payload.SourceUrl);
            return null;
        }
        finally
        {
            DeleteUnlessRetained(payload);
        }
    }

    /// <summary>Downloads are throw-away once uploaded; the media cache's own copies are not (see <see cref="MediaPayload.RetainFiles"/>).</summary>
    private void DeleteUnlessRetained(MediaPayload payload)
    {
        if (payload.RetainFiles)
        {
            return;
        }

        foreach (var item in payload.Items)
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
