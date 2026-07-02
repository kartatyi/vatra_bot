using LeBot.Infrastructure.Configuration;

namespace LeBot.Infrastructure.Diagnostics;

/// <summary>
/// The effective extraction config, distilled to the handful of fields an operator needs to see at
/// startup. No secrets, no Telegram-user PII — just whether cookies are on, the resolved yt-dlp path,
/// the upload cap, and the environment. <see cref="StartupConfigSummary.Describe"/> builds it.
/// </summary>
public sealed record EffectiveConfig(
    bool CookiesEnabled,
    string? CookiesBrowser,
    string YtDlpPath,
    int MaxFileSizeMb,
    string Environment);

/// <summary>Builds the one-line startup summary (P4) from the bound options.</summary>
public static class StartupConfigSummary
{
    public static EffectiveConfig Describe(YtDlpOptions ytDlp, string environment) => new(
        CookiesEnabled: CookieAccessAdvisor.CookiesEnabled(ytDlp.CookiesFromBrowser),
        CookiesBrowser: ytDlp.CookiesFromBrowser,
        YtDlpPath: ExecutablePathResolver.Resolve(ytDlp.BinaryPath),
        MaxFileSizeMb: ytDlp.MaxFileSizeMb,
        Environment: environment);
}
