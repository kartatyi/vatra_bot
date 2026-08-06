using LeBot.Infrastructure.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.Diagnostics;

/// <summary>
/// Logs one Information line summarising the effective extraction config the moment the host starts —
/// so a glance at the log answers "are cookies on, which yt-dlp, what upload cap, which environment?"
/// without guessing. When cookies are configured but the process is LocalSystem it escalates to a
/// Warning, because that exact combination is the silent-failure trap this whole feature exists to kill.
/// Runs first among the hosted services so the summary precedes any polling noise.
/// </summary>
internal sealed class StartupConfigLogger(
    IOptions<YtDlpOptions> ytDlpOptions,
    IHostEnvironment environment,
    IHostAccountInfo account,
    ILogger<StartupConfigLogger> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var ytDlp = ytDlpOptions.Value;
        var summary = StartupConfigSummary.Describe(ytDlp, environment.EnvironmentName);

        logger.LogInformation(
            "Startup config: cookies={Cookies}, ytDlpPath={YtDlpPath}, maxUploadMb={MaxUploadMb}, environment={Environment}",
            summary.CookiesEnabled ? summary.CookiesBrowser : "disabled",
            summary.YtDlpPath,
            summary.MaxFileSizeMb,
            summary.Environment);

        if (CookieAccessAdvisor.ShouldWarnUnreadable(account.IsLocalSystem, ytDlp.CookiesFromBrowser))
        {
            // ShouldWarnUnreadable returns true only when CookiesFromBrowser is set, so it is non-null here.
            logger.LogWarning("{CookieWarning}", CookieAccessAdvisor.UnreadableWarning(ytDlp.CookiesFromBrowser!));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
