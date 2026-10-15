using LeBot.Application.Ports;
using LeBot.Domain.Media;

namespace LeBot.Infrastructure.Telegram;

/// <summary>
/// <see cref="IInlineMediaUploader"/> over the storage chat (<see cref="TelegramMediaVault"/>). Only the
/// first item counts: an inline result is a single file — Telegram has no inline albums. A payload the
/// media cache already put in Telegram goes through without a second upload.
/// </summary>
internal sealed class TelegramInlineMediaUploader(TelegramMediaVault vault) : IInlineMediaUploader
{
    public async Task<(MediaKind Kind, string FileId)?> UploadAsync(MediaPayload payload, CancellationToken cancellationToken)
    {
        var first = payload with { Items = payload.Items.Take(1).ToList() };
        if (await vault.StoreAsync(first, cancellationToken) is [{ TelegramFileId: { } fileId } item])
        {
            DeleteDownloads(payload);
            return (item.Kind, fileId);
        }

        DeleteDownloads(payload);
        return null;
    }

    /// <summary>Downloads are throw-away once the upload is done or has failed; store-owned files are not touched.</summary>
    private static void DeleteDownloads(MediaPayload payload)
    {
        if (payload.RetainFiles)
        {
            return;
        }

        foreach (var item in payload.Items.Where(i => i.FilePath.Length > 0))
        {
            try
            {
                File.Delete(item.FilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort: the downloads sweep empties the folder hourly anyway.
            }
        }
    }
}
