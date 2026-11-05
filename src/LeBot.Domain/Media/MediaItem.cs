namespace LeBot.Domain.Media;

/// <summary>
/// A single piece of media to be sent to Telegram — a video, photo, audio clip, or animation.
/// </summary>
/// <param name="FilePath">
/// Absolute path to the local file containing the media bytes; empty for an item that lives in Telegram
/// already (see <see cref="TelegramFileId"/>).
/// </param>
/// <param name="Kind">Whether the media is a video, photo, audio, or animation.</param>
/// <param name="MimeType">Mime type, e.g. "video/mp4". Optional; helps Telegram pick the right rendering.</param>
/// <param name="SizeBytes">File size in bytes; null when unknown.</param>
/// <param name="DurationSeconds">Duration in seconds for video and audio; null when not applicable.</param>
public sealed record MediaItem(
    string FilePath,
    MediaKind Kind,
    string? MimeType,
    long? SizeBytes,
    int? DurationSeconds)
{
    /// <summary>
    /// Telegram's id for this file when Telegram already holds it — uploaded to the bot's storage chat
    /// once, then sent anywhere by id, instantly and without touching the disk. Null for a fresh download.
    /// </summary>
    public string? TelegramFileId { get; init; }
}
