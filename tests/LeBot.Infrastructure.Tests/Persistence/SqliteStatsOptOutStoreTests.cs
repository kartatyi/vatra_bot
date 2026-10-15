using LeBot.Application.Telemetry;
using LeBot.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LeBot.Infrastructure.Tests.Persistence;

public sealed class SqliteStatsOptOutStoreTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"lebot-optout-{Path.GetRandomFileName()}.db");

    private readonly DbContextOptions<LeBotDbContext> _options;
    private readonly SqliteStatsOptOutStore _sut;

    public SqliteStatsOptOutStoreTests()
    {
        _options = new DbContextOptionsBuilder<LeBotDbContext>().UseSqlite($"Data Source={_dbPath}").Options;
        using var db = new LeBotDbContext(_options);
        db.Database.Migrate();
        _sut = new SqliteStatsOptOutStore(new TestDbContextFactory(_options));
    }

    [Fact]
    public async Task SetOptedOutAsync_True_RecordsTheHashedChatAndUser()
    {
        await _sut.SetOptedOutAsync(-100123, 7, optedOut: true, CancellationToken.None);

        (await RowsAsync()).Should().Equal((ChatHasher.Of(-100123), 7L));
    }

    [Fact]
    public async Task GetOptedOutAsync_ListsThatChatsOptOutsOnly()
    {
        await _sut.SetOptedOutAsync(-100123, 7, optedOut: true, CancellationToken.None);
        await _sut.SetOptedOutAsync(-100999, 8, optedOut: true, CancellationToken.None);

        (await _sut.GetOptedOutAsync(-100123, CancellationToken.None)).Should().ContainSingle().Which.Should().Be(7);
        (await _sut.IsOptedOutAsync(-100123, 7, CancellationToken.None)).Should().BeTrue();
        (await _sut.IsOptedOutAsync(-100123, 8, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task SetOptedOutAsync_TrueTwice_IsIdempotent()
    {
        await _sut.SetOptedOutAsync(-100123, 7, optedOut: true, CancellationToken.None);
        await _sut.SetOptedOutAsync(-100123, 7, optedOut: true, CancellationToken.None);

        (await RowsAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task SetOptedOutAsync_FalseAfterTrue_RemovesTheRow()
    {
        await _sut.SetOptedOutAsync(-100123, 7, optedOut: true, CancellationToken.None);

        await _sut.SetOptedOutAsync(-100123, 7, optedOut: false, CancellationToken.None);

        (await RowsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task SetOptedOutAsync_FalseWhenNeverOptedOut_DoesNothing()
    {
        var optIn = () => _sut.SetOptedOutAsync(-100123, 7, optedOut: false, CancellationToken.None);

        await optIn.Should().NotThrowAsync();
    }

    private async Task<List<(string, long)>> RowsAsync()
    {
        await using var db = new LeBotDbContext(_options);
        var rows = await db.StatsOptOuts.ToListAsync();
        return rows.Select(r => (r.ChatHash, r.UserId)).ToList();
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
