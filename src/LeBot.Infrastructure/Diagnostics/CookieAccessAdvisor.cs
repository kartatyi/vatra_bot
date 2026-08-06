namespace LeBot.Infrastructure.Diagnostics;

/// <summary>
/// Pure rules for the most expensive first-run trap: <c>YtDlp:CookiesFromBrowser</c> is set, but the
/// bot runs as LocalSystem — an account that has no browser profile, so yt-dlp can never read the
/// cookies and login-gated extraction (Instagram, X) silently returns nothing. The advice here turns
/// that into a loud, actionable warning at startup and in <c>--doctor</c>.
/// </summary>
public static class CookieAccessAdvisor
{
    public static bool CookiesEnabled(string? cookiesFromBrowser) =>
        !string.IsNullOrWhiteSpace(cookiesFromBrowser);

    /// <summary>
    /// True when cookies are configured but the current account is LocalSystem — the combination that
    /// cannot work. LocalSystem has no per-user browser store for yt-dlp to borrow cookies from.
    /// </summary>
    public static bool ShouldWarnUnreadable(bool isLocalSystem, string? cookiesFromBrowser) =>
        isLocalSystem && CookiesEnabled(cookiesFromBrowser);

    public static string UnreadableWarning(string browser) =>
        $"YtDlp:CookiesFromBrowser is '{browser}', but the bot is running as LocalSystem, which has no "
        + $"'{browser}' profile to read cookies from — login-gated extraction (e.g. Instagram) will fail. "
        + "Run the bot as the interactive user who has the logged-in browser instead of LocalSystem.";
}
