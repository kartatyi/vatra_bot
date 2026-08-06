using System.Data.Common;
using LeBot.Infrastructure.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.Persistence;

/// <summary>
/// Applies pending EF Core migrations once at startup so the telemetry schema exists — and stays in
/// step with the model after a self-update — before the dispatcher records anything. Registered ahead
/// of the poll loop so the database is ready first. A failure here is logged, not fatal: the bot's core
/// job is reposting, and it must keep running even when the journal can't be opened (read-only volume,
/// locked file, full disk) — the only cost is that run's telemetry.
/// </summary>
internal sealed class RepostDatabaseInitializer(
    IDbContextFactory<LeBotDbContext> contextFactory,
    IOptions<TelemetryOptions> options,
    ILogger<RepostDatabaseInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var databasePath = options.Value.ResolvedDatabasePath;

        try
        {
            // SQLite won't create missing parent directories; the migration would fail without this.
            var directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            await db.Database.MigrateAsync(cancellationToken);

            // WAL lets a reader (the dashboard, or a manual sqlite3 session) run concurrently with the
            // bot's writes instead of colliding on the single rollback journal. The mode is persisted in
            // the database header, so setting it once here applies to every connection thereafter.
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);

            logger.LogInformation("Telemetry database ready at {DatabasePath}", databasePath);
        }
        catch (Exception ex) when (ex is SqliteException or DbException or IOException or UnauthorizedAccessException)
        {
            logger.LogError(
                ex,
                "Could not initialise telemetry database at {DatabasePath}; repost journalling is disabled this run",
                databasePath);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
