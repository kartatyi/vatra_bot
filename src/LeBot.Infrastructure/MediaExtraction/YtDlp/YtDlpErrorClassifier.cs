namespace LeBot.Infrastructure.MediaExtraction.YtDlp;

/// <summary>
/// Maps yt-dlp's stderr text onto the handful of failure shapes we react to differently.
/// yt-dlp has no machine-readable error codes, so string matching is the only signal available.
/// </summary>
public static class YtDlpErrorClassifier
{
    public static bool LooksLikeUnsupportedUrl(string detail) =>
        detail.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when yt-dlp raised the error from its catch-all <c>[generic]</c> extractor rather than a
    /// site-specific one. yt-dlp tags every error with the extractor that produced it, which is the only
    /// signal telling "a platform we repost is broken" (<c>[TikTok]</c>, <c>[Instagram]</c>) apart from
    /// "this was never a media page" — a shop, news, or blog link the generic extractor probed and got a
    /// 403 or a paywall from. The latter must stay silent and out of the failure counters, or the
    /// dashboard's red turns into noise that hides real breakage.
    /// </summary>
    public static bool IsGenericExtractorFailure(string detail) =>
        detail.Contains("[generic]", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// TikTok gates access behind a JS challenge that yt-dlp solves with a native Python
    /// implementation. That solver only clears the check probabilistically — measured ~33%
    /// success per attempt — so the same URL fails and then succeeds on a bare retry.
    /// Installing a Deno JS runtime does not help: the TikTok solver stays on the Python path.
    /// </summary>
    public static bool IsTransientChallengeFailure(string detail) =>
        detail.Contains("Unable to extract universal data for rehydration", StringComparison.OrdinalIgnoreCase)
        || detail.Contains("Unexpected response from webpage request", StringComparison.OrdinalIgnoreCase);
}
