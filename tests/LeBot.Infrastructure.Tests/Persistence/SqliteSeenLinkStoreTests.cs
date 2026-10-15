using LeBot.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LeBot.Infrastructure.Tests.Persistence;

public sealed class SqliteSeenLinkStoreTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"lebot-seen-{Path.GetRandomFileName()}.db");

    private readonly DbContextOptions<LeBotDbContext> _options;
    private readonly SqliteSeenLinkStore _sut;

    public SqliteSeenLinkStoreTests()
    {
        _options = new DbContextOptionsBuilder<LeBotDbContext>().UseSqlite($"Data Source={_dbPath}").Options;
        using var db = new LeBotDbContext(_options);
        db.Database.Migrate();
        _sut = new SqliteSeenLinkStore(
            new TestDbContextFactory(_options),
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero)),
            NullLogger<SqliteSeenLinkStore>.Instance);
    }

    [Fact]
    public async Task MarkSeenAsync_FirstTime_IsNotARepeat_SecondTimeIs()
    {
        var url = new Uri("https://www.tiktok.com/@a/video/1");

        var first = await _sut.MarkSeenAsync(-100123, url, 1, CancellationToken.None);
        var second = await _sut.MarkSeenAsync(-100123, url, 2, CancellationToken.None);

        first.Should().BeFalse();
        second.Should().BeTrue();
    }

    [Fact]
    public async Task MarkSeenAsync_RecordsEverySightingWithItsSender()
    {
        var url = new Uri("https://www.tiktok.com/@a/video/1");

        await _sut.MarkSeenAsync(-100123, url, 1, CancellationToken.None);
        await _sut.MarkSeenAsync(-100123, url, null, CancellationToken.None);

        await using var db = new LeBotDbContext(_options);
        var sightings = await db.LinkSightings.OrderBy(s => s.Id).Select(s => new { s.SenderId, s.IsRepeat }).ToListAsync();
        sightings.Should().Equal(new { SenderId = (long?)1, IsRepeat = false }, new { SenderId = (long?)null, IsRepeat = true });
    }

    [Fact]
    public async Task MarkSeenAsync_ShareLinksDifferingOnlyByTracking_AreTheSameLink()
    {
        await _sut.MarkSeenAsync(-100123, new Uri("https://www.instagram.com/reel/abc/?igshid=one"), 1, CancellationToken.None);

        var repeat = await _sut.MarkSeenAsync(-100123, new Uri("https://instagram.com/reel/abc?igshid=two"), 2, CancellationToken.None);

        repeat.Should().BeTrue();
    }

    [Fact]
    public async Task MarkSeenAsync_SameLinkInAnotherChat_IsNotARepeat()
    {
        var url = new Uri("https://x.com/a/status/1");
        await _sut.MarkSeenAsync(-100123, url, 1, CancellationToken.None);

        (await _sut.MarkSeenAsync(-100999, url, 1, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task MarkSeenAsync_DatabaseUnreadable_AnswersNotARepeat()
    {
        SqliteConnection.ClearAllPools();
        File.WriteAllText(_dbPath, "not a database");

        var repeat = await _sut.MarkSeenAsync(-100123, new Uri("https://x.com/a/status/1"), 1, CancellationToken.None);

        repeat.Should().BeFalse();
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
