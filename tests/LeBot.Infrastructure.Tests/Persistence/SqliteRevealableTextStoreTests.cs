using LeBot.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LeBot.Infrastructure.Tests.Persistence;

public sealed class SqliteRevealableTextStoreTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"lebot-reveal-{Path.GetRandomFileName()}.db");

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly SqliteRevealableTextStore _sut;

    public SqliteRevealableTextStoreTests()
    {
        var options = new DbContextOptionsBuilder<LeBotDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;

        using var db = new LeBotDbContext(options);
        db.Database.Migrate();

        _sut = new SqliteRevealableTextStore(
            new TestDbContextFactory(options), _time, NullLogger<SqliteRevealableTextStore>.Instance);
    }

    [Fact]
    public async Task TakeAsync_AfterSave_ReturnsTheTextOnce()
    {
        await _sut.SaveAsync(-100123, 42, "the recipe", CancellationToken.None);

        var first = await _sut.TakeAsync(-100123, 42, CancellationToken.None);
        var second = await _sut.TakeAsync(-100123, 42, CancellationToken.None);

        first.Should().Be("the recipe");
        second.Should().BeNull();
    }

    [Fact]
    public async Task TakeAsync_OtherMessageOrChat_FindsNothing()
    {
        await _sut.SaveAsync(-100123, 42, "the recipe", CancellationToken.None);

        (await _sut.TakeAsync(-100123, 43, CancellationToken.None)).Should().BeNull();
        (await _sut.TakeAsync(-100999, 42, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task TakeAsync_PastLifetime_ReturnsNull()
    {
        await _sut.SaveAsync(-100123, 42, "the recipe", CancellationToken.None);
        _time.Advance(SqliteRevealableTextStore.Lifetime + TimeSpan.FromMinutes(1));

        (await _sut.TakeAsync(-100123, 42, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_SweepsExpiredEntries()
    {
        await _sut.SaveAsync(-100123, 1, "old", CancellationToken.None);
        _time.Advance(SqliteRevealableTextStore.Lifetime + TimeSpan.FromMinutes(1));

        await _sut.SaveAsync(-100123, 2, "new", CancellationToken.None);

        await using var db = new LeBotDbContext(
            new DbContextOptionsBuilder<LeBotDbContext>().UseSqlite($"Data Source={_dbPath}").Options);
        (await db.RevealableTexts.Select(r => r.MessageId).ToListAsync()).Should().Equal(2);
    }

    [Fact]
    public async Task SaveAsync_SameMessageTwice_DoesNotThrow()
    {
        await _sut.SaveAsync(-100123, 42, "first", CancellationToken.None);

        var save = () => _sut.SaveAsync(-100123, 42, "second", CancellationToken.None);

        await save.Should().NotThrowAsync();
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
