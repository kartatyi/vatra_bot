using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.Tests.Persistence;

/// <summary>
/// Verifies the startup initializer applies the EF Core migrations against a fresh path — the
/// guarantee that a new install (or a self-update that ships a new migration) comes up with a usable
/// schema instead of crashing on first write.
/// </summary>
public sealed class RepostDatabaseInitializerTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"lebot-init-{Path.GetRandomFileName()}");

    [Fact]
    public async Task StartAsync_AppliesMigrations_ToAQueryableEmptySchema()
    {
        var databasePath = Path.Combine(_directory, "lebot.db");
        var options = Options.Create(new TelemetryOptions { DatabasePath = databasePath });
        var dbOptions = new DbContextOptionsBuilder<LeBotDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;

        var initializer = new RepostDatabaseInitializer(
            new TestDbContextFactory(dbOptions),
            options,
            NullLogger<RepostDatabaseInitializer>.Instance);

        await initializer.StartAsync(CancellationToken.None);

        // The initializer creates the missing parent directory and the database file itself...
        File.Exists(databasePath).Should().BeTrue();

        // ...and the migration has run, so the table exists and is queryable (empty).
        await using var db = new LeBotDbContext(dbOptions);
        (await db.RepostEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task StartAsync_EnablesWalJournalMode()
    {
        var databasePath = Path.Combine(_directory, "lebot.db");
        var options = Options.Create(new TelemetryOptions { DatabasePath = databasePath });
        var dbOptions = new DbContextOptionsBuilder<LeBotDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;

        var initializer = new RepostDatabaseInitializer(
            new TestDbContextFactory(dbOptions),
            options,
            NullLogger<RepostDatabaseInitializer>.Instance);

        await initializer.StartAsync(CancellationToken.None);

        // WAL is persisted in the database header, so a fresh connection sees it — that's what makes a
        // concurrent dashboard reader safe against the bot's writes.
        await using var db = new LeBotDbContext(dbOptions);
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        var mode = (string?)await command.ExecuteScalarAsync();

        mode.Should().Be("wal");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
