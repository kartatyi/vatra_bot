using LeBot.Application.UseCases.AnswerInlineQuery;
using LeBot.Domain.Media;
using LeBot.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LeBot.Infrastructure.Tests.Persistence;

public sealed class SqlitePreparedPostStoreTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"lebot-prepared-{Path.GetRandomFileName()}.db");

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly SqlitePreparedPostStore _sut;

    public SqlitePreparedPostStoreTests()
    {
        var options = new DbContextOptionsBuilder<LeBotDbContext>().UseSqlite($"Data Source={_dbPath}").Options;
        using var db = new LeBotDbContext(options);
        db.Database.Migrate();
        _sut = new SqlitePreparedPostStore(new TestDbContextFactory(options), _time, NullLogger<SqlitePreparedPostStore>.Instance);
    }

    [Fact]
    public async Task SaveThenTryGet_RoundTrips_AcrossTrackingVariants()
    {
        var post = new PreparedPost(MediaKind.Video, "file-1", "body");
        await _sut.SaveAsync(new Uri("https://www.instagram.com/reel/abc/?igshid=one"), post, CancellationToken.None);

        var found = await _sut.TryGetAsync(new Uri("https://instagram.com/reel/abc?igshid=two"), CancellationToken.None);

        found.Should().Be(post);
    }

    [Fact]
    public async Task TryGet_TextOnlyPost_RoundTripsWithoutAFile()
    {
        var url = new Uri("https://x.com/a/status/1");
        await _sut.SaveAsync(url, new PreparedPost(null, null, "words"), CancellationToken.None);

        (await _sut.TryGetAsync(url, CancellationToken.None)).Should().Be(new PreparedPost(null, null, "words"));
    }

    [Fact]
    public async Task TryGet_PastLifetime_IsAMiss()
    {
        var url = new Uri("https://x.com/a/status/1");
        await _sut.SaveAsync(url, new PreparedPost(MediaKind.Photo, "p", null), CancellationToken.None);
        _time.Advance(SqlitePreparedPostStore.Lifetime + TimeSpan.FromMinutes(1));

        (await _sut.TryGetAsync(url, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Save_SameLinkAgain_ReplacesTheEntry()
    {
        var url = new Uri("https://x.com/a/status/1");
        await _sut.SaveAsync(url, new PreparedPost(MediaKind.Photo, "old", null), CancellationToken.None);

        await _sut.SaveAsync(url, new PreparedPost(MediaKind.Photo, "new", null), CancellationToken.None);

        (await _sut.TryGetAsync(url, CancellationToken.None))!.FileId.Should().Be("new");
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
