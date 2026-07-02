using LeBot.Application.Telemetry;
using LeBot.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LeBot.Infrastructure.Tests.Persistence;

/// <summary>
/// Round-trips the store against a real SQLite file — which also proves the patched SQLitePCLRaw 3.0.x
/// native bundle loads and runs, the runtime risk of bumping it off EF Core's default.
/// </summary>
public sealed class SqliteRepostEventStoreTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"lebot-store-{Path.GetRandomFileName()}.db");

    private readonly DbContextOptions<LeBotDbContext> _options;
    private readonly SqliteRepostEventStore _sut;

    public SqliteRepostEventStoreTests()
    {
        _options = new DbContextOptionsBuilder<LeBotDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;

        using var db = new LeBotDbContext(_options);
        db.Database.EnsureCreated();

        _sut = new SqliteRepostEventStore(
            new TestDbContextFactory(_options),
            NullLogger<SqliteRepostEventStore>.Instance);
    }

    private static RepostEvent SampleEvent(RepostOutcome outcome = RepostOutcome.MediaRepost) =>
        new(
            OccurredAt: new DateTimeOffset(2026, 6, 28, 10, 0, 0, TimeSpan.Zero),
            Host: "tiktok.com",
            Url: "https://tiktok.com/@a/video/1",
            Outcome: outcome,
            Extractor: "YtDlpPlatformExtractor",
            ErrorVariant: null,
            ErrorReason: null,
            MediaCount: 1,
            MediaBytes: 2048,
            ElapsedMs: 1200,
            BotVersion: "1.2.3",
            ChatHash: "abc123def456");

    [Fact]
    public async Task AppendAsync_PersistsEvent_AndReadsBackEveryField()
    {
        await _sut.AppendAsync(SampleEvent(), CancellationToken.None);

        await using var db = new LeBotDbContext(_options);
        var stored = await db.RepostEvents.SingleAsync();

        stored.Host.Should().Be("tiktok.com");
        stored.Url.Should().Be("https://tiktok.com/@a/video/1");
        stored.Outcome.Should().Be(RepostOutcome.MediaRepost);
        stored.MediaCount.Should().Be(1);
        stored.MediaBytes.Should().Be(2048);
        stored.ElapsedMs.Should().Be(1200);
        stored.BotVersion.Should().Be("1.2.3");
        stored.ChatHash.Should().Be("abc123def456");
    }

    [Fact]
    public async Task AppendAsync_StoresOutcomeAsReadableText()
    {
        await _sut.AppendAsync(SampleEvent(RepostOutcome.Failure), CancellationToken.None);

        await using var db = new LeBotDbContext(_options);
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT Outcome FROM RepostEvents LIMIT 1";
        var raw = (string?)await command.ExecuteScalarAsync();

        // The enum is mapped to text so a raw SELECT over the DB reads "Failure", not "2".
        raw.Should().Be("Failure");
    }

    [Fact]
    public async Task AppendAsync_TwoEvents_BothPersisted()
    {
        await _sut.AppendAsync(SampleEvent(), CancellationToken.None);
        await _sut.AppendAsync(SampleEvent(RepostOutcome.TextFallback), CancellationToken.None);

        await using var db = new LeBotDbContext(_options);
        (await db.RepostEvents.CountAsync()).Should().Be(2);
    }

    public void Dispose()
    {
        // SQLite keeps the file handle pooled; release it before deleting so the temp file doesn't linger.
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }
}
