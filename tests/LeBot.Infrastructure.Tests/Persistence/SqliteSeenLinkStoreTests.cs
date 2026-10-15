using LeBot.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LeBot.Infrastructure.Tests.Persistence;

public sealed class SqliteSeenLinkStoreTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"lebot-seen-{Path.GetRandomFileName()}.db");

    private readonly SqliteSeenLinkStore _sut;

    public SqliteSeenLinkStoreTests()
    {
        var options = new DbContextOptionsBuilder<LeBotDbContext>().UseSqlite($"Data Source={_dbPath}").Options;
        using var db = new LeBotDbContext(options);
        db.Database.Migrate();
        _sut = new SqliteSeenLinkStore(
            new TestDbContextFactory(options),
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero)),
            NullLogger<SqliteSeenLinkStore>.Instance);
    }

    [Fact]
    public async Task MarkSeenAsync_FirstTime_IsNotARepeat_SecondTimeIs()
    {
        var url = new Uri("https://www.tiktok.com/@a/video/1");

        var first = await _sut.MarkSeenAsync(-100123, url, CancellationToken.None);
        var second = await _sut.MarkSeenAsync(-100123, url, CancellationToken.None);

        first.Should().BeFalse();
        second.Should().BeTrue();
    }

    [Fact]
    public async Task MarkSeenAsync_ShareLinksDifferingOnlyByTracking_AreTheSameLink()
    {
        await _sut.MarkSeenAsync(-100123, new Uri("https://www.instagram.com/reel/abc/?igshid=one"), CancellationToken.None);

        var repeat = await _sut.MarkSeenAsync(-100123, new Uri("https://instagram.com/reel/abc?igshid=two"), CancellationToken.None);

        repeat.Should().BeTrue();
    }

    [Fact]
    public async Task MarkSeenAsync_SameLinkInAnotherChat_IsNotARepeat()
    {
        var url = new Uri("https://x.com/a/status/1");
        await _sut.MarkSeenAsync(-100123, url, CancellationToken.None);

        (await _sut.MarkSeenAsync(-100999, url, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task MarkSeenAsync_DatabaseUnreadable_AnswersNotARepeat()
    {
        SqliteConnection.ClearAllPools();
        File.WriteAllText(_dbPath, "not a database");

        var repeat = await _sut.MarkSeenAsync(-100123, new Uri("https://x.com/a/status/1"), CancellationToken.None);

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
