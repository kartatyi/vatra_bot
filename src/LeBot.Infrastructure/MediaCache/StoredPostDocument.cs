using LeBot.Domain.Media;

namespace LeBot.Infrastructure.MediaCache;

/// <summary>A cached post as <see cref="TelegramMediaCache"/> serialises it: everything but the bytes, which live in Telegram.</summary>
internal sealed record StoredPostDocument(
    string NormalizedUrl,
    Uri SourceUrl,
    string Extractor,
    string? Title,
    string? Author,
    string? Description,
    IReadOnlyList<StoredMediaItem> Items);

/// <summary>One media item of a <see cref="StoredPostDocument"/>, referenced by its Telegram file id.</summary>
internal sealed record StoredMediaItem(
    string FileId,
    MediaKind Kind,
    string? MimeType,
    long? SizeBytes,
    int? DurationSeconds);
