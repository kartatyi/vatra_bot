namespace LeBot.Infrastructure.MediaProcessing;

/// <summary>
/// The encoder settings that squeeze a video of a known length under a byte cap: the highest video
/// bitrate that still fits, the audio bitrate it is shared with, and the longest side the picture keeps.
/// Fitting comes first and the picture second, so the bitrate is sized for the encoder's worst case,
/// not its average. Resolution falls with the bitrate because x264 spends a starved bitrate on blocks
/// at full size but on a clean, smaller picture once scaled down.
/// </summary>
/// <param name="VideoKbps">Average video bitrate the encoder aims for, and its ceiling.</param>
/// <param name="AudioKbps">AAC bitrate; zero when the source has no audio track.</param>
/// <param name="MaxLongSide">Longest side the output may keep; a smaller source is never upscaled.</param>
internal sealed record CompressionPlan(int VideoKbps, int AudioKbps, int MaxLongSide)
{
    /// <summary>
    /// Share of the cap the streams may take; the rest is the mp4 container's own index and headers,
    /// which come to well under one percent.
    /// </summary>
    internal const double StreamShare = 0.97;

    /// <summary>
    /// The encoder's rate buffer, in seconds of <see cref="VideoKbps"/>. Capped at that rate, the video
    /// can overshoot an exact rate × duration by at most one full buffer — so the plan reserves it.
    /// </summary>
    internal const int BufferSeconds = 2;

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

        var audioKbps = hasAudio ? AudioKbpsWhenPresent : 0;
        var videoBits = maxBytes * 8 * StreamShare - audioKbps * 1000.0 * durationSeconds;
        var videoKbps = (int)Math.Floor(videoBits / (durationSeconds + BufferSeconds) / 1000);

        return WithVideoKbps(videoKbps, audioKbps);
    }

    /// <summary>
    /// A second plan for when an encode still came out at <paramref name="producedBytes"/>, over
    /// <paramref name="maxBytes"/>: the bitrate scaled down by the overshoot, with a further margin so
    /// the retry lands inside. <c>null</c> when that would drop below a watchable bitrate.
    /// </summary>
    public CompressionPlan? ShrunkToFit(long producedBytes, long maxBytes)
    {
        var videoKbps = (int)Math.Floor(VideoKbps * (double)maxBytes / producedBytes * 0.9);
        return WithVideoKbps(videoKbps, AudioKbps);
    }

    private static CompressionPlan? WithVideoKbps(int videoKbps, int audioKbps)
    {
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
