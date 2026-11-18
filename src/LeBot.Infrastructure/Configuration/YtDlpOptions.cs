namespace LeBot.Infrastructure.Configuration;

/// <summary>
/// Bound from the <c>YtDlp</c> section of configuration.
/// </summary>
public sealed class YtDlpOptions
{
    public const string SectionName = "YtDlp";

    /// <summary>Path to the yt-dlp binary. Fetched by <c>tools/fetch-tools.ps1</c>.</summary>
    public string BinaryPath { get; init; } = "tools/yt-dlp/yt-dlp.exe";

    /// <summary>
    /// Optional ffmpeg path; required for formats yt-dlp must merge or transcode, and for clips of live
    /// streams — without it a live link gets its text only.
    /// </summary>
    public string? FfmpegPath { get; init; }

    /// <summary>
    /// Browser name (firefox / chrome / edge / brave / ...) whose session cookies yt-dlp should
    /// borrow. Lets the bot see content gated behind login on platforms like Instagram (image
    /// posts) and X. Leave null for anonymous extraction. Set via user-secrets or env:
    /// <c>dotnet user-secrets set "YtDlp:CookiesFromBrowser" "firefox" --project src/LeBot.Host</c>.
    /// </summary>
    public string? CookiesFromBrowser { get; init; }

    /// <summary>Where downloaded files land. Cleaned up after sending.</summary>
    public string DownloadDirectory { get; init; } = "downloads";

    /// <summary>
    /// <see cref="DownloadDirectory"/> as an absolute path. A relative value is rebased onto the
    /// executable's own directory rather than the launch working directory, so downloads land beside
    /// the binary no matter where the process is started from (the same reason the log path is pinned).
    /// An absolute value passes through unchanged. Every consumer — both embed extractors, the yt-dlp
    /// extractor, and the cleanup sweep — uses this so they always agree on one location.
    /// </summary>
    public string ResolvedDownloadDirectory => Path.IsPathRooted(DownloadDirectory)
        ? DownloadDirectory
        : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, DownloadDirectory));

    /// <summary>
    /// Hard cap on file size in megabytes. Above this we skip the download to save bandwidth
    /// and avoid Telegram's 50&#160;MB upload ceiling.
    /// </summary>
    public int MaxFileSizeMb { get; init; } = 50;

    /// <summary>
    /// The longest a metadata fetch may take, retries included. Past it the link counts as failed and
    /// yt-dlp's whole process tree is killed: links are handled one by one, so a hung fetch would stall
    /// every chat behind it.
    /// </summary>
    public int MetadataTimeoutSeconds { get; init; } = 90;

    /// <summary>The longest a download may take, retries included; past it, as for <see cref="MetadataTimeoutSeconds"/>.</summary>
    public int DownloadTimeoutSeconds { get; init; } = 300;

    /// <summary>
    /// How many seconds of a live stream to record — from the moment the link is handled, not from the
    /// stream's start — before sending them as a round video. Telegram caps those at a minute.
    /// </summary>
    public int LiveClipSeconds { get; init; } = 30;
}
