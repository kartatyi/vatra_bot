namespace LeBot.Domain.Media;

/// <summary>
/// A single piece of media to be sent to Telegram — a video, photo, audio clip, or animation.
/// </summary>
/// <param name="FilePath">Absolute path to the local file containing the media bytes.</param>
/// <param name="Kind">Whether the media is a video, photo, audio, or animation.</param>
/// <param name="MimeType">Mime type, e.g. "video/mp4". Optional; helps Telegram pick the right rendering.</param>
/// <param name="SizeBytes">File size in bytes; null when unknown.</param>
/// <param name="DurationSeconds">Duration in seconds for video and audio; null when not applicable.</param>
public sealed record MediaItem(
    string FilePath,
    MediaKind Kind,
    string? MimeType,
    long? SizeBytes,
    int? DurationSeconds);
