namespace LeBot.Infrastructure.MediaCache;

/// <summary>
/// The <c>entry.json</c> that sits beside a cache entry's media files and describes them.
/// Everything needed to rebuild the original payload without touching the network.
/// </summary>
/// <param name="SchemaVersion">Bumped whenever this shape changes; older entries are then discarded rather than misread.</param>
/// <param name="NormalizedUrl">The key this entry was filed under — re-checked on read so a hash collision can't serve the wrong content.</param>
/// <param name="SourceUrl">The URL as posted, replayed into the rebuilt payload.</param>
/// <param name="Extractor">Type name of the extractor that produced the payload, so metrics stay attributed on a hit.</param>
/// <param name="Title">Payload title, verbatim.</param>
/// <param name="Author">Payload author, verbatim.</param>
/// <param name="Description">Payload description, verbatim.</param>
/// <param name="CachedAtUtc">When the entry was written — the clock the lifetime is measured from.</param>
/// <param name="Items">The stored media files, in send order.</param>
/// <param name="FollowUps">The chained parts of the post, in order; empty for an ordinary post.</param>
internal sealed record CachedPayloadDocument(
    int SchemaVersion,
    string NormalizedUrl,
    Uri SourceUrl,
    string Extractor,
    string? Title,
    string? Author,
    string? Description,
    DateTimeOffset CachedAtUtc,
    IReadOnlyList<CachedMediaItem> Items,
    IReadOnlyList<CachedSegment> FollowUps)
{
    // 2: added FollowUps. A v1 entry has no idea whether its post continued, and serving half a
    // thread is worse than re-extracting it, so the reader drops them.
    public const int CurrentSchemaVersion = 2;
}

/// <summary>One chained part of a cached post: its text and its own stored media files.</summary>
internal sealed record CachedSegment(string? Text, IReadOnlyList<CachedMediaItem> Items);
