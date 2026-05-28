using LeBot.Domain.Common;
using LeBot.Domain.Media;

namespace LeBot.Application.Ports;

/// <summary>
/// A strategy that pulls a <see cref="MediaPayload"/> out of a URL it claims to support.
/// One implementation per platform (or one universal yt-dlp adapter); the dispatcher
/// asks each in turn via <see cref="CanHandle"/>.
/// </summary>
public interface IPlatformExtractor
{
    /// <summary>Whether this extractor can produce a payload for the given URL.</summary>
    bool CanHandle(Uri url);

    /// <summary>
    /// Produce a payload for <paramref name="url"/>. Returns
    /// <see cref="Result{TValue, TError}.Err"/> when the URL is not supported,
    /// the content is gone, or the underlying tool failed.
    /// </summary>
    Task<Result<MediaPayload, ExtractionError>> ExtractAsync(Uri url, CancellationToken cancellationToken);
}
