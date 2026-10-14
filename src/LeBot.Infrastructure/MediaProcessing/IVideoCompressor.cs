using LeBot.Domain.Common;
using LeBot.Domain.Media;

namespace LeBot.Infrastructure.MediaProcessing;

/// <summary>
/// Re-encodes a downloaded video that came out over Telegram's upload cap so it can still be posted.
/// Kept behind an interface so the extractor's decision of <em>when</em> to compress is testable
/// without spawning the encoder.
/// </summary>
internal interface IVideoCompressor
{
    /// <summary>
    /// Writes an h264/AAC mp4 copy of <paramref name="sourcePath"/> no larger than
    /// <paramref name="maxBytes"/> and returns its absolute path. The source file is left in place.
    /// </summary>
    Task<Result<string, ExtractionError>> CompressAsync(
        string sourcePath,
        long maxBytes,
        CancellationToken cancellationToken);
}
