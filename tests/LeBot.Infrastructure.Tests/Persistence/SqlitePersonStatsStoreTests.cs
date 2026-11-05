using LeBot.Application.Telemetry;
using LeBot.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LeBot.Infrastructure.Tests.Persistence;

public sealed class SqlitePersonStatsStoreTests : IDisposable
{
    private const string Chat = "chat-a";
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"lebot-people-{Path.GetRandomFileName()}.db");

    private readonly DbContextOptions<LeBotDbContext> _options;
    private readonly SqlitePersonStatsStore _sut;

    public SqlitePersonStatsStoreTests()
    {
        _options = new DbContextOptionsBuilder<LeBotDbContext>().UseSqlite($"Data Source={_dbPath}").Options;
        using var db = new LeBotDbContext(_options);
        db.Database.Migrate();
        _sut = new SqlitePersonStatsStore(new TestDbContextFactory(_options));
    }

    [Fact]
    public async Task GetRepostMomentsAsync_ReturnsSuccessfulRepostsOfPeopleInTheWindow()
    {
        await SeedEventsAsync(
            Repost(1, T0),
            Repost(1, T0.AddDays(-40)),
            Repost(2, T0, RepostOutcome.Failure),
            Repost(null, T0),
            Repost(3, T0, chat: "chat-b"));

        var moments = await _sut.GetRepostMomentsAsync(Chat, T0.AddDays(-30), CancellationToken.None);

        moments.Should().Equal(new SenderMoment(1, T0));
    }

    [Fact]
    public async Task GetRepeatCountsAsync_CountsRepeatSightingsPerPerson()
    {
        await SeedSightingsAsync(
            Sighting("a", 1, repeat: false),
            Sighting("a", 2, repeat: true),
            Sighting("a", 2, repeat: true),
            Sighting("b", 1, repeat: true),
            Sighting("a", null, repeat: true));

        var counts = await _sut.GetRepeatCountsAsync(Chat, DateTimeOffset.MinValue, CancellationToken.None);

        counts.Should().BeEquivalentTo(new[] { new SenderCount(2, 2), new SenderCount(1, 1) });
    }

    [Fact]
    public async Task GetTrendsetterCountsAsync_CountsFirstsThatSomeoneElseRepeated()
    {
        await SeedSightingsAsync(
            Sighting("a", 1, repeat: false),
            Sighting("a", 2, repeat: true),   // 1 started a trend
            Sighting("b", 1, repeat: false),
            Sighting("b", 1, repeat: true),   // 1 echoing themself — no trend
            Sighting("c", 2, repeat: false)); // nobody followed

        var counts = await _sut.GetTrendsetterCountsAsync(Chat, DateTimeOffset.MinValue, CancellationToken.None);

        counts.Should().Equal(new SenderCount(1, 1));
    }

    [Fact]
    public async Task EveryRead_LeavesOutWhoOptedOut()
    {
        await SeedEventsAsync(Repost(1, T0), Repost(2, T0));
        await SeedSightingsAsync(Sighting("a", 2, repeat: false), Sighting("a", 1, repeat: true), Sighting("b", 2, repeat: true));
        await using (var db = new LeBotDbContext(_options))
        {
            db.StatsOptOuts.Add(new StatsOptOutRow { ChatHash = Chat, UserId = 2 });
            await db.SaveChangesAsync();
        }

        var moments = await _sut.GetRepostMomentsAsync(Chat, DateTimeOffset.MinValue, CancellationToken.None);
        var repeats = await _sut.GetRepeatCountsAsync(Chat, DateTimeOffset.MinValue, CancellationToken.None);
        var trends = await _sut.GetTrendsetterCountsAsync(Chat, DateTimeOffset.MinValue, CancellationToken.None);

        moments.Select(m => m.SenderId).Should().Equal(1);
        repeats.Select(r => r.SenderId).Should().Equal(1);
        trends.Should().BeEmpty();
    }

    private static RepostEvent Repost(long? sender, DateTimeOffset at, RepostOutcome outcome = RepostOutcome.MediaRepost, string chat = Chat) =>
        new(at, "tiktok.com", "https://tiktok.com/x", outcome, "YtDlpPlatformExtractor", null, null, 1, null, 100, "1.0.0", chat, sender);

    private static LinkSightingRow Sighting(string link, long? sender, bool repeat) =>
        new() { ChatHash = Chat, LinkKey = link, SenderId = sender, SeenAt = T0, IsRepeat = repeat };

    private async Task SeedEventsAsync(params RepostEvent[] events)
    {
        await using var db = new LeBotDbContext(_options);
        db.RepostEvents.AddRange(events);
        await db.SaveChangesAsync();
    }

    private async Task SeedSightingsAsync(params LinkSightingRow[] sightings)
    {
        await using var db = new LeBotDbContext(_options);
        db.LinkSightings.AddRange(sightings);
        await db.SaveChangesAsync();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }
}
