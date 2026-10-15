using LeBot.Domain.Media;

namespace LeBot.Application.Ports;

/// <summary>
/// Puts a post's media into Telegram so an inline answer can reference it: an inline result can only
/// point at a file Telegram already holds.
/// </summary>
public interface IInlineMediaUploader
{
    /// <summary>
    /// Uploads the payload's first media item and returns its kind and Telegram file id, or
    /// <see langword="null"/> when the upload failed. Files the payload doesn't retain are deleted
    /// afterwards, as after any send.
    /// </summary>
    Task<(MediaKind Kind, string FileId)?> UploadAsync(MediaPayload payload, CancellationToken cancellationToken);
}
