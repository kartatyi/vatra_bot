using LeBot.Domain.Media;

namespace LeBot.Infrastructure.MediaExtraction.Threads;

/// <summary>
/// Fetches the social card Threads renders for crawlers — its <c>og:image</c> — as a downloaded
/// photo. The card is a picture <em>of</em> the post: author header, body text as pixels, and a
/// collage strip of whatever the post attaches. That makes it the right repost for a text-only post
/// and a lie for every other kind; deciding which is which is the extractor's call, this only fetches.
/// </summary>
internal interface IThreadsCardLoader
{
    /// <summary>
    /// The card as a photo on disk, or null when the page offers none or it won't download. Expected
    /// failures are logged and swallowed — the caller falls back to the post's text.
    /// </summary>
    Task<MediaItem?> LoadAsync(Uri url, CancellationToken cancellationToken);
}
