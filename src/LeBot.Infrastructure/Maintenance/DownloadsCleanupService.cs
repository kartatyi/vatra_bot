using LeBot.Infrastructure.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.Maintenance;

/// <summary>
/// Sweeps the yt-dlp <c>DownloadDirectory</c> on an interval and deletes anything older than
/// the configured age. The messenger already does best-effort cleanup right after sending,
/// but a crash between download and send (process killed, OOM, machine reboot) leaves
/// orphans that would otherwise accumulate forever.
/// </summary>
public sealed class DownloadsCleanupService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MaxFileAge = TimeSpan.FromHours(1);

    private readonly YtDlpOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DownloadsCleanupService> _logger;

    public DownloadsCleanupService(
        IOptions<YtDlpOptions> options,
        TimeProvider timeProvider,
        ILogger<DownloadsCleanupService> logger)
    {
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Sweep();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Downloads cleanup sweep failed");
            }

            try
            {
                await Task.Delay(SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // internal rather than private so the cutoff logic can be exercised directly in a unit
    // test without spinning up the background loop and its real-time interval timer.
    internal void Sweep()
    {
        if (!Directory.Exists(_options.DownloadDirectory))
        {
            return;
        }

        var cutoff = _timeProvider.GetUtcNow().UtcDateTime - MaxFileAge;
        var deleted = 0;

        foreach (var path in Directory.EnumerateFiles(_options.DownloadDirectory))
        {
            try
            {
                var lastWrite = File.GetLastWriteTimeUtc(path);
                if (lastWrite < cutoff)
                {
                    File.Delete(path);
                    deleted++;
                }
            }
            catch (IOException) { /* file in use; next sweep */ }
            catch (UnauthorizedAccessException) { /* skipped */ }
        }

        if (deleted > 0)
        {
            _logger.LogInformation(
                "Cleanup: deleted {Count} orphaned download(s) older than {MaxAgeMinutes}min",
                deleted, (int)MaxFileAge.TotalMinutes);
        }
    }
}
