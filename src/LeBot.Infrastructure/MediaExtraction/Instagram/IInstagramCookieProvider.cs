namespace LeBot.Infrastructure.MediaExtraction.Instagram;

/// <summary>
/// Supplies Instagram session cookies for the private web API. Implementations source them from the
/// operator's logged-in browser (the same <c>YtDlp:CookiesFromBrowser</c> the rest of the bot uses)
/// and cache them, refreshing on demand when the API rejects a stale session.
/// </summary>
internal interface IInstagramCookieProvider
{
    /// <summary>
    /// Returns the current cookies, or <c>null</c> when none are configured or none could be read.
    /// Pass <paramref name="forceRefresh"/> after an auth failure to bypass the cache and re-read
    /// the browser.
    /// </summary>
    Task<InstagramCookies?> GetAsync(bool forceRefresh, CancellationToken cancellationToken);
}
