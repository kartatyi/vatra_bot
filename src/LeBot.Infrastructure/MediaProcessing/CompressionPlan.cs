namespace LeBot.Infrastructure.MediaProcessing;

/// <summary>
/// The encoder settings that squeeze a video of a known length under a byte cap: a video bitrate
/// that fills the cap, the audio bitrate it is shared with, and the longest side the picture keeps.
/// Resolution falls with the bitrate because x264 spends a starved bitrate on blocks at full size
/// but on a clean, smaller picture once scaled down.
/// </summary>
/// <param name="VideoKbps">Average video bitrate the encoder aims for.</param>
/// <param name="AudioKbps">AAC bitrate; zero when the source has no audio track.</param>
/// <param name="MaxLongSide">Longest side the output may keep; a smaller source is never upscaled.</param>
internal sealed record CompressionPlan(int VideoKbps, int AudioKbps, int MaxLongSide)
{
    /// <summary>
    /// Share of the cap the encoder aims for. Single-pass ABR lands within a few percent of its target
    /// either way, and the mp4 container adds its own index on top — the rest is that margin.
    /// </summary>
    internal const double CapShare = 0.9;

    internal const int AudioKbpsWhenPresent = 128;

    /// <summary>
    /// Below this, even a 480p picture smears into blocks. A video that would need less is too long
    /// for the cap, and a caption reply serves the chat better than a slideshow of artefacts.
    /// </summary>
    internal const int MinVideoKbps = 400;

    /// <summary>
    /// Plans the encode, or returns <c>null</c> when the video is too long to fit
    /// <paramref name="maxBytes"/> at a watchable bitrate.
    /// </summary>
    public static CompressionPlan? For(double durationSeconds, bool hasAudio, long maxBytes)
    {
        if (durationSeconds <= 0 || maxBytes <= 0)
        {
            return null;
        }

        var totalKbps = maxBytes * CapShare * 8 / durationSeconds / 1000;
        var audioKbps = hasAudio ? AudioKbpsWhenPresent : 0;
        var videoKbps = (int)Math.Floor(totalKbps) - audioKbps;

        if (videoKbps < MinVideoKbps)
        {
            return null;
        }

        var maxLongSide = videoKbps switch
        {
            >= 2500 => 1920,
            >= 1000 => 1280,
            _ => 854,
        };

        return new CompressionPlan(videoKbps, audioKbps, maxLongSide);
    }
}
