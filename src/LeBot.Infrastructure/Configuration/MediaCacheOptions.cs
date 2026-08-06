namespace LeBot.Infrastructure.Configuration;

/// <summary>
/// Bound from the <c>MediaCache</c> section of configuration. Governs the on-disk store of
/// already-extracted reposts: a repeat of the same link is answered from these files instead of
/// going back to the platform.
/// </summary>
public sealed class MediaCacheOptions
{
    public const string SectionName = "MediaCache";

    /// <summary>Turns the cache off entirely — every link is then extracted fresh, as before.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Where cache entries live, one directory per URL. Kept deliberately separate from
    /// <see cref="YtDlpOptions.DownloadDirectory"/>, which the downloads sweep empties after an hour.
    /// </summary>
    public string Directory { get; init; } = "cache";

    /// <summary>
    /// How long an entry stays servable, measured from when it was written. Past it the entry is a
    /// miss and gets deleted — that's what keeps the bot from reposting a video the author has since
    /// edited or taken down.
    /// </summary>
    public int TtlHours { get; init; } = 24;

    /// <summary>
    /// Ceiling on the whole cache. Once crossed, the oldest entries are evicted until it fits again,
    /// so a busy chat can't fill the disk before the entries age out on their own.
    /// </summary>
    public int MaxTotalSizeMb { get; init; } = 4096;

    /// <summary>
    /// <see cref="Directory"/> as an absolute path. A relative value is rebased onto the executable's
    /// own directory rather than the launch working directory, for the same reason the download and
    /// log paths are — so the cache lands beside the binary no matter where the process is started.
    /// </summary>
    public string ResolvedDirectory => Path.IsPathRooted(Directory)
        ? Directory
        : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Directory));

    /// <summary><see cref="TtlHours"/> as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan Ttl => TimeSpan.FromHours(TtlHours);

    /// <summary><see cref="MaxTotalSizeMb"/> in bytes.</summary>
    public long MaxTotalSizeBytes => (long)MaxTotalSizeMb * 1024 * 1024;
}
