namespace LeBot.Domain.Media;

/// <summary>
/// Everything an <c>IPlatformExtractor</c> produced from a source URL: the original URL,
/// optional title, description, and author, and one or more <see cref="MediaItem"/>s ready to ship to Telegram.
/// </summary>
/// <param name="SourceUrl">The URL the user posted in the chat.</param>
/// <param name="Title">A human-readable title pulled from the platform's metadata; may be null.</param>
/// <param name="Author">The uploader / channel / handle; may be null.</param>
/// <param name="Items">One or more media items. Empty payloads are allowed but signal "nothing to repost".</param>
/// <param name="Description">The post's caption / body text from the platform; may be null. Often more useful than <paramref name="Title"/> on Instagram, TikTok, and Threads.</param>
public sealed record MediaPayload(
    Uri SourceUrl,
    string? Title,
    string? Author,
    IReadOnlyList<MediaItem> Items,
    string? Description = null)
{
    /// <summary>True when the payload contains at least one media item.</summary>
    public bool HasMedia => Items.Count > 0;
}
