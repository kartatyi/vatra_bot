using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.MediaCache;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.Maintenance;

/// <summary>
/// Ages the media cache out on an interval: entries past their lifetime are deleted even if nobody
/// ever posts that link again. <see cref="FileSystemMediaCache"/> also prunes on every write, but a
/// quiet bot never writes — and a day-old video is meant to be gone whether or not the chat is busy.
/// </summary>
public sealed class MediaCacheCleanupService(
    FileSystemMediaCache cache,
    IOptions<MediaCacheOptions> options,
    ILogger<MediaCacheCleanupService> logger)
    : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation("Media cache is disabled; every link will be extracted fresh");
            return;
        }

        logger.LogInformation(
            "Media cache: {Directory}, entries live {TtlHours}h, capped at {MaxSizeMb}MB",
            settings.ResolvedDirectory, settings.TtlHours, settings.MaxTotalSizeMb);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                cache.Prune();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Media cache sweep failed");
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
}
