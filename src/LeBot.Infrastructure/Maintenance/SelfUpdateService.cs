using LeBot.Application.Ports;
using LeBot.Application.Releases;
using LeBot.Domain.Common;
using LeBot.Infrastructure.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.Maintenance;

/// <summary>
/// Keeps the bot binary current from GitHub Releases (ADR&#160;0002). Mirrors
/// <see cref="YtDlpUpdateService"/>'s startup-delay-then-loop shape. On startup it promotes a
/// just-applied update (deletes the <c>.bak</c>, DMs the operator); each sweep it compares its own
/// version to the latest release and either notifies or downloads, verifies, swaps, and stops so the
/// relaunch helper can bring the new binary up under Task Scheduler.
/// </summary>
internal sealed class SelfUpdateService(
    IReleaseSource releaseSource,
    IUpdateInstaller installer,
    IAppVersion appVersion,
    ITelegramMessenger messenger,
    IHostApplicationLifetime lifetime,
    IOptions<UpdateOptions> options,
    ILogger<SelfUpdateService> logger)
    : BackgroundService
{
    private static readonly ReleaseVersion DevVersion = new(0, 0, 0);

    private readonly UpdateOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Self-update is Windows-only (it relaunches via Task Scheduler) and never touches a dev
        // build (0.0.0), which would otherwise try to "update" over a `dotnet run` working copy.
        if (!_options.Enabled || !OperatingSystem.IsWindows() || appVersion.Current == DevVersion)
        {
            logger.LogDebug(
                "Self-update disabled (enabled={Enabled}, windows={Windows}, version={Version})",
                _options.Enabled, OperatingSystem.IsWindows(), appVersion.Current);
            return;
        }

        await PromoteIfJustUpdatedAsync(stoppingToken);

        try
        {
            await Task.Delay(TimeSpan.FromMinutes(_options.StartupDelayMinutes), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var action = await RunOnceAsync(stoppingToken);
                if (action == UpdateAction.Apply)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Self-update sweep failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(_options.CheckIntervalHours), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal async Task<UpdateAction> RunOnceAsync(CancellationToken cancellationToken)
    {
        var current = appVersion.Current;

        var latestResult = await releaseSource.GetLatestAsync(cancellationToken);
        if (latestResult is Result<ReleaseInfo, ReleaseSourceError>.Err err)
        {
            if (err.Error == ReleaseSourceError.NoReleases)
            {
                logger.LogDebug("No releases available yet");
            }
            else
            {
                logger.LogWarning("Could not fetch latest release: {Error}", err.Error);
            }

            return UpdateAction.None;
        }

        var release = ((Result<ReleaseInfo, ReleaseSourceError>.Ok)latestResult).Value;
        var action = UpdateDecision.Evaluate(
            _options.Enabled, current, release.Version, _options.Mode == UpdateMode.Apply);

        switch (action)
        {
            case UpdateAction.None:
                logger.LogDebug("Up to date on {Version}", current);
                return UpdateAction.None;

            case UpdateAction.Notify:
                await NotifyAsync($"Update v{release.Version} available.", cancellationToken);
                return UpdateAction.Notify;

            case UpdateAction.Apply:
                return await ApplyAsync(current, release, cancellationToken);

            default:
                return UpdateAction.None;
        }
    }

    private async Task<UpdateAction> ApplyAsync(
        ReleaseVersion current,
        ReleaseInfo release,
        CancellationToken cancellationToken)
    {
        await NotifyAsync($"Updating to v{release.Version}…", cancellationToken);

        var staged = await installer.DownloadAndVerifyAsync(release, cancellationToken);
        if (staged is Result<string, UpdateInstallError>.Err err)
        {
            if (err.Error == UpdateInstallError.ShaMismatch)
            {
                logger.LogError("Update to {Version} failed SHA256 verification", release.Version);
                await NotifyAsync(
                    $"Update verification failed, staying on v{current}.", cancellationToken);
            }
            else
            {
                logger.LogWarning("Update to {Version} could not be staged: {Error}", release.Version, err.Error);
            }

            return UpdateAction.None;
        }

        var stagedPath = ((Result<string, UpdateInstallError>.Ok)staged).Value;
        installer.ApplyAndLaunchHelper(stagedPath, release.Version);
        logger.LogInformation("Stopping to hand off to the new binary v{Version}", release.Version);
        lifetime.StopApplication();
        return UpdateAction.Apply;
    }

    private async Task PromoteIfJustUpdatedAsync(CancellationToken cancellationToken)
    {
        string markerPath;
        string backupPath;
        try
        {
            markerPath = UpdatePaths.MarkerPath;
            backupPath = UpdatePaths.BackupPath;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Could not resolve update paths for startup promotion");
            return;
        }

        if (!File.Exists(markerPath))
        {
            return;
        }

        var markerVersion = await ReadMarkerVersionAsync(markerPath, cancellationToken);
        if (markerVersion is null || markerVersion != appVersion.Current)
        {
            return;
        }

        await DeleteWithRetryAsync(backupPath, cancellationToken);
        TryDelete(markerPath);

        logger.LogInformation("Promoted update to v{Version}", appVersion.Current);
        await NotifyAsync($"Updated to v{appVersion.Current}.", cancellationToken);
    }

    private async Task<ReleaseVersion?> ReadMarkerVersionAsync(string markerPath, CancellationToken cancellationToken)
    {
        try
        {
            var raw = (await File.ReadAllTextAsync(markerPath, cancellationToken)).Trim();
            return ReleaseVersion.Parse(raw).Match<ReleaseVersion?>(version => version, _ => null);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not read update marker {Path}", markerPath);
            return null;
        }
    }

    private async Task DeleteWithRetryAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return;
                }

                File.Delete(path);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogDebug(ex, "Could not delete {Path}", path);
                return;
            }
        }

        logger.LogWarning("Gave up deleting {Path} after retries", path);
    }

    private async Task NotifyAsync(string text, CancellationToken cancellationToken)
    {
        if (_options.NotifyChatId is not { } chatId)
        {
            return;
        }

        try
        {
            await messenger.SendTextAsync(chatId, text, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to send update notification");
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Could not delete {Path}", path);
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogDebug(ex, "Could not delete {Path}", path);
        }
    }
}
