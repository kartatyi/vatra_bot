using LeBot.Domain.Media;

namespace LeBot.Infrastructure.MediaCache;

/// <summary>
/// One media file inside a cache entry. Mirrors <see cref="MediaItem"/> except for the path: entries
/// store a bare file name and resolve it against their own directory, so the whole cache survives
/// being moved — or the bot being reinstalled somewhere else.
/// </summary>
/// <param name="FileName">Name of the file inside the entry directory.</param>
/// <param name="Kind">Video / photo / animation / audio.</param>
/// <param name="MimeType">Mime type as reported by the extractor; may be null.</param>
/// <param name="SizeBytes">File size in bytes; null when unknown.</param>
/// <param name="DurationSeconds">Duration for video and audio; null when not applicable.</param>
internal sealed record CachedMediaItem(
    string FileName,
    MediaKind Kind,
    string? MimeType,
    long? SizeBytes,
    int? DurationSeconds);
