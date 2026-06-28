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
/// <see cref="YtDlpUpdateService"/>'s startup-delay-then-loop shape. On startup it runs the health gate
/// over any just-applied update — waiting until the bot actually confirms it is serving before it
/// either promotes (deletes the <c>.bak</c>, DMs the operator) or, if the new build never starts
/// serving, rolls back to the previous binary. Each sweep it then compares its own version to the
/// latest release and either notifies or downloads, verifies, swaps, and stops so the relaunch helper
/// can bring the new binary up under Task Scheduler.
/// </summary>
internal sealed class SelfUpdateService(
    IReleaseSource releaseSource,
    IUpdateInstaller installer,
    IAppVersion appVersion,
    ITelegramMessenger messenger,
    BotHealthSignal health,
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

        if (!await RunHealthGateAsync(stoppingToken))
        {
            return;
        }

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

    /// <summary>
    /// Decides the fate of a just-applied update. Returns <c>true</c> if the service should carry on to
    /// its regular release-check loop, or <c>false</c> if a rollback was started and the host is stopping.
    /// </summary>
    private async Task<bool> RunHealthGateAsync(CancellationToken stoppingToken)
    {
        string markerPath;
        try
        {
            markerPath = UpdatePaths.MarkerPath;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Could not resolve update paths for the health gate");
            return true;
        }

        if (!File.Exists(markerPath))
        {
            return true;
        }

        var current = appVersion.Current;
        var pendingVersion = await ReadMarkerVersionAsync(markerPath, stoppingToken);
        if (pendingVersion is null || pendingVersion != current)
        {
            // Either an unparseable marker, or we are running an older binary than the marker names
            // (we already rolled back). Clear the stale state so a future update starts from clean.
            if (pendingVersion is not null)
            {
                logger.LogDebug(
                    "Update marker names v{Pending} but we are v{Current}; clearing stale state",
                    pendingVersion, current);
            }

            ClearProbationState();
            return true;
        }

        var served = await AwaitServingAsync(stoppingToken);
        if (served is null)
        {
            return true; // shutting down before we could decide
        }

        var decision = UpdateWatchdog.Evaluate(
            pendingMatchesCurrent: true,
            isHealthy: served.Value,
            healthStampPresent: false,
            healthDeadlinePassed: !served.Value,
            backupAvailable: File.Exists(UpdatePaths.BackupPath));

        switch (decision)
        {
            case WatchdogDecision.Promote:
                await PromoteAsync(current, stoppingToken);
                return true;

            case WatchdogDecision.RollBack:
                await RollBackAsync(current, stoppingToken);
                return false;

            default:
                logger.LogCritical(
                    "v{Version} has not confirmed it is serving within {Minutes} min and there is no .bak to roll back to",
                    current, _options.HealthGateTimeoutMinutes);
                await NotifyAsync(
                    $"v{current} has not confirmed it is serving, and there is no previous binary to roll back to.",
                    stoppingToken);
                return true;
        }
    }

    /// <summary>Returns <c>true</c> if the bot confirmed it is serving, <c>false</c> on timeout, <c>null</c> on shutdown.</summary>
    private async Task<bool?> AwaitServingAsync(CancellationToken stoppingToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(_options.HealthGateTimeoutMinutes));

        try
        {
            await health.WaitForServingAsync(timeoutCts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return stoppingToken.IsCancellationRequested ? null : false;
        }
    }

    private async Task PromoteAsync(ReleaseVersion current, CancellationToken cancellationToken)
    {
        // Stamp first: if the process dies mid-promotion (before the .bak is gone), the next boot
        // sees the stamp and treats the build as proven rather than rolling a healthy binary back.
        TryWriteHealthStamp(current);
        await DeleteWithRetryAsync(UpdatePaths.BackupPath, cancellationToken);
        ClearProbationState();

        logger.LogInformation("Promoted update to v{Version}", current);
        await NotifyAsync($"Updated to v{current}.", cancellationToken);
    }

    private async Task RollBackAsync(ReleaseVersion failed, CancellationToken cancellationToken)
    {
        logger.LogCritical(
            "v{Version} did not confirm it is serving within {Minutes} min; rolling back to the previous binary",
            failed, _options.HealthGateTimeoutMinutes);

        try
        {
            installer.RestoreBackupAndLaunchHelper();
        }
        catch (IOException ex)
        {
            logger.LogError(ex, "In-process rollback could not restore the previous binary");
            await NotifyAsync($"v{failed} failed its health check and the rollback failed too — manual recovery needed.", cancellationToken);
            return;
        }

        await NotifyAsync($"v{failed} failed its health check — rolled back to the previous version.", cancellationToken);
        lifetime.StopApplication();
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

    private void TryWriteHealthStamp(ReleaseVersion version)
    {
        try
        {
            File.WriteAllText(UpdatePaths.HealthStampPath, version.ToString());
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Could not write health stamp");
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogDebug(ex, "Could not write health stamp");
        }
    }

    private void ClearProbationState()
    {
        TryDelete(UpdatePaths.MarkerPath);
        TryDelete(UpdatePaths.HealthStampPath);
        TryDelete(UpdatePaths.BootAttemptsPath);
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
