namespace LeBot.Infrastructure.Diagnostics;

/// <summary>
/// The synchronous, side-effect-light half of the <c>--doctor</c> checklist: config loaded, token
/// present, log directory writable, cookies/account sanity. The network- and process-backed checks
/// (Telegram <c>getMe</c>, yt-dlp <c>--version</c>) live in <see cref="TelegramProbe"/> and
/// <see cref="YtDlpProbe"/>.
/// </summary>
public static class StartupChecks
{
    /// <summary>
    /// Confirms configuration actually loaded by checking a Serilog sink resolved. With the embedded
    /// defaults as the base layer this should never fail — if it does, the embedded resource is gone.
    /// </summary>
    public static DoctorCheck Configuration(bool serilogConfigured) =>
        serilogConfigured
            ? DoctorCheck.Pass("Configuration", "loaded (embedded defaults + on-disk overrides)")
            : DoctorCheck.Fail("Configuration", "no Serilog sink resolved — configuration did not load");

    public static DoctorCheck Token(string? botToken) =>
        string.IsNullOrWhiteSpace(botToken)
            ? DoctorCheck.Fail(
                "Bot token",
                "missing — set Telegram__BotToken (env) or Telegram:BotToken in appsettings.Local.json")
            : DoctorCheck.Pass("Bot token", "present");

    /// <summary>
    /// Proves the resolved log directory is writable by creating, writing, and deleting a probe file —
    /// the difference between "logs will appear" and the silent-failure trap of nowhere to write them.
    /// </summary>
    public static DoctorCheck LogDirectory(string absoluteLogDirectory)
    {
        try
        {
            Directory.CreateDirectory(absoluteLogDirectory);
            var probe = Path.Combine(
                absoluteLogDirectory,
                $".doctor-write-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return DoctorCheck.Pass("Log directory", absoluteLogDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DoctorCheck.Fail("Log directory", $"{absoluteLogDirectory} is not writable — {ex.Message}");
        }
    }

    public static DoctorCheck Cookies(string? cookiesFromBrowser, bool isLocalSystem)
    {
        if (!CookieAccessAdvisor.CookiesEnabled(cookiesFromBrowser))
        {
            return DoctorCheck.Pass("Browser cookies", "disabled (anonymous extraction)");
        }

        // CookiesEnabled is true, so the value is non-null/non-whitespace here.
        var browser = cookiesFromBrowser!;
        return CookieAccessAdvisor.ShouldWarnUnreadable(isLocalSystem, browser)
            ? DoctorCheck.Warn("Browser cookies", CookieAccessAdvisor.UnreadableWarning(browser))
            : DoctorCheck.Pass("Browser cookies", $"enabled ({browser}); readable by the current account");
    }
}
