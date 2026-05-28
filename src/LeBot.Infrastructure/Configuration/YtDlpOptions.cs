namespace LeBot.Infrastructure.Configuration;

/// <summary>
/// Bound from the <c>YtDlp</c> section of configuration.
/// </summary>
public sealed class YtDlpOptions
{
    public const string SectionName = "YtDlp";

    /// <summary>Path to the yt-dlp binary. Fetched by <c>tools/fetch-tools.ps1</c>.</summary>
    public string BinaryPath { get; init; } = "tools/yt-dlp/yt-dlp.exe";

    /// <summary>Optional ffmpeg path; required only for formats yt-dlp must merge or transcode.</summary>
    public string? FfmpegPath { get; init; }

    /// <summary>Where downloaded files land. Cleaned up after sending.</summary>
    public string DownloadDirectory { get; init; } = "downloads";

    /// <summary>
    /// Hard cap on file size in megabytes. Above this we skip the download to save bandwidth
    /// and avoid Telegram's 50&#160;MB upload ceiling.
    /// </summary>
    public int MaxFileSizeMb { get; init; } = 50;
}
