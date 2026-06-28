namespace LeBot.Infrastructure.MediaExtraction.Instagram;

/// <summary>
/// Exports a browser's cookie store to Netscape-format jar lines. Split out from
/// <see cref="YtDlpCookieProvider"/> so the caching and parsing orchestration stays unit-testable
/// without spawning a real yt-dlp process.
/// </summary>
internal interface IBrowserCookieJarReader
{
    /// <summary>Returns the cookie jar's lines, or <c>null</c> when the export failed.</summary>
    Task<IReadOnlyList<string>?> ReadAsync(string browser, CancellationToken cancellationToken);
}
