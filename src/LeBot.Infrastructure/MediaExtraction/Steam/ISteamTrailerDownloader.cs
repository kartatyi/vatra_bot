using LeBot.Domain.Common;
using LeBot.Domain.Media;

namespace LeBot.Infrastructure.MediaExtraction.Steam;

/// <summary>
/// Turns one rendition of a Steam trailer into a single playable mp4 on disk. Kept behind an
/// interface so the extractor's choice of <em>what</em> to download is testable without spawning
/// the tool that does the downloading.
/// </summary>
internal interface ISteamTrailerDownloader
{
    /// <summary>
    /// Downloads the <paramref name="height"/>-pixel video rendition of <paramref name="manifestUrl"/>
    /// with its audio, merged into mp4. Returns the file's absolute path.
    /// </summary>
    Task<Result<string, ExtractionError>> DownloadAsync(
        Uri manifestUrl,
        int height,
        CancellationToken cancellationToken);
}
