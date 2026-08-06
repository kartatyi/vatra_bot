using System.Text;
using LeBot.Host.Configuration;
using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.Diagnostics;

namespace LeBot.Host.Diagnostics;

/// <summary>
/// The <c>--doctor</c> verb: a one-shot, read-only self-check that turns the silent-failure trap into a
/// ✓/✗ checklist. It loads config the same way the running host does, then verifies the basics — config
/// loaded, token present, yt-dlp runnable, Telegram reachable, log directory writable, cookies sane for
/// the current account. Exit code is non-zero if any check fails, so it doubles as a smoke test in CI or
/// a post-deploy script. Run after <c>--install</c> automatically, or any time with
/// <c>LeBot.Host.exe --doctor</c>.
/// </summary>
internal static class Doctor
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    public static async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        EnableUtf8Output();
        Console.WriteLine("=== LeBot doctor ===");
        Console.WriteLine();

        var baseDirectory = AppContext.BaseDirectory;
        var configuration = StandaloneConfiguration.ForExecutable(baseDirectory);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        var checks = await GatherAsync(configuration, baseDirectory, timeout.Token);
        WriteReport(Console.Out, checks);

        return checks.Any(c => c.Status == DoctorStatus.Fail) ? 1 : 0;
    }

    /// <summary>
    /// Runs every check and returns the results. Shared with the post-install self-check. Pass
    /// <paramref name="account"/> to evaluate the cookies/account check against an identity other than
    /// the current process — the installer passes the LocalSystem service it registers, which is what
    /// the bot will actually run as, not the elevated admin running <c>--install</c>.
    /// </summary>
    public static async Task<IReadOnlyList<DoctorCheck>> GatherAsync(
        IConfiguration configuration,
        string baseDirectory,
        CancellationToken cancellationToken,
        IHostAccountInfo? account = null)
    {
        account ??= new HostAccountInfo();
        var telegram = configuration.GetSection(TelegramOptions.SectionName).Get<TelegramOptions>()
            ?? new TelegramOptions();
        var ytDlp = configuration.GetSection(YtDlpOptions.SectionName).Get<YtDlpOptions>()
            ?? new YtDlpOptions();
        var serilogConfigured = configuration.GetSection("Serilog:WriteTo").GetChildren().Any();
        var logDirectory = LogPathResolver.ResolveLogDirectory(configuration, baseDirectory);

        var checks = new List<DoctorCheck>
        {
            StartupChecks.Configuration(serilogConfigured),
            StartupChecks.Token(telegram.BotToken),
            StartupChecks.LogDirectory(logDirectory),
            StartupChecks.Cookies(ytDlp.CookiesFromBrowser, account.IsLocalSystem),
            await YtDlpProbe.CheckAsync(ytDlp, cancellationToken),
        };

        checks.Add(string.IsNullOrWhiteSpace(telegram.BotToken)
            ? DoctorCheck.Warn("Telegram API", "skipped — no token to test getMe")
            : await TelegramProbe.CheckAsync(telegram.BotToken, cancellationToken));

        return checks;
    }

    /// <summary>Prints the checklist plus a one-line verdict. Shared with the post-install self-check.</summary>
    public static void WriteReport(TextWriter writer, IReadOnlyList<DoctorCheck> checks)
    {
        foreach (var check in checks)
        {
            writer.WriteLine($"  {check.Symbol} {check.Name}: {check.Detail}");
        }

        writer.WriteLine();
        var failures = checks.Count(c => c.Status == DoctorStatus.Fail);
        var warnings = checks.Count(c => c.Status == DoctorStatus.Warn);

        writer.WriteLine((failures, warnings) switch
        {
            (0, 0) => "All checks passed.",
            (0, _) => $"Basics OK — {warnings} warning(s) above need your attention.",
            _ => $"{failures} check(s) failed — the bot will not work correctly until they're fixed.",
        });
    }

    /// <summary>
    /// Best-effort switch the console to UTF-8 so the ✓/⚠/✗ glyphs render on legacy code pages. A
    /// redirected stream has no console to retune; the glyphs degrade harmlessly, so we swallow that.
    /// </summary>
    internal static void EnableUtf8Output()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
        }
    }
}
