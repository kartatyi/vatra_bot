using LeBot.Application.Caching;
using LeBot.Domain.Media;

namespace LeBot.Application.Ports;

/// <summary>
/// A short-lived store of already-extracted reposts, keyed by source URL. A hit short-circuits the
/// entire extraction chain — no HTTP request, no yt-dlp process, no headless browser — so the second
/// time a link makes the rounds in a chat the bot answers from local disk.
/// </summary>
public interface IMediaCache
{
    /// <summary>
    /// The stored repost for <paramref name="url"/>, or <c>null</c> when nothing is cached, the entry
    /// has aged past its lifetime, or its files went missing. A broken cache degrades to a miss —
    /// this never throws, because a caching problem must not cost the user their repost.
    /// </summary>
    Task<CachedRepost?> TryGetAsync(Uri url, CancellationToken cancellationToken);

    /// <summary>
    /// Stores <paramref name="payload"/> under its source URL and returns the payload to send from here
    /// on: the same post, its media referenced however the store keeps it — the caller's own files for a
    /// store that copies them, or files Telegram already holds for one that uploads them. Best-effort:
    /// failures are logged, never thrown, and answer with <paramref name="payload"/> unchanged.
    /// </summary>
    /// <param name="payload">A payload with media or replyable text; hollow ones aren't worth storing.</param>
    /// <param name="extractor">Type name of the extractor that produced it, replayed on every hit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<MediaPayload> SaveAsync(MediaPayload payload, string extractor, CancellationToken cancellationToken);
}
