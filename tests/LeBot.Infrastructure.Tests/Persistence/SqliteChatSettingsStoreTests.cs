using LeBot.Domain.Settings;
using LeBot.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LeBot.Infrastructure.Tests.Persistence;

public sealed class SqliteChatSettingsStoreTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"lebot-settings-{Path.GetRandomFileName()}.db");

    private readonly SqliteChatSettingsStore _sut;

    public SqliteChatSettingsStoreTests()
    {
        var options = new DbContextOptionsBuilder<LeBotDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;

        using var db = new LeBotDbContext(options);
        db.Database.Migrate();

        _sut = new SqliteChatSettingsStore(
            new TestDbContextFactory(options),
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero)),
            NullLogger<SqliteChatSettingsStore>.Instance);
    }

    [Fact]
    public async Task GetAsync_NeverSaved_ReturnsDefault()
    {
        var settings = await _sut.GetAsync(-100123, CancellationToken.None);

        settings.Should().Be(ChatSettings.Default);
    }

    [Fact]
    public async Task SaveAsync_ThenGetAsync_RoundTrips()
    {
        var saved = new ChatSettings(CaptionMode.Brief, CaptionMode.Brief);

        await _sut.SaveAsync(-100123, saved, CancellationToken.None);

        (await _sut.GetAsync(-100123, CancellationToken.None)).Should().Be(saved);
    }

    [Fact]
    public async Task SaveAsync_ShrugOnFailure_RoundTrips()
    {
        var saved = ChatSettings.Default with { ShrugOnFailure = true };

        await _sut.SaveAsync(-100123, saved, CancellationToken.None);

        (await _sut.GetAsync(-100123, CancellationToken.None)).ShrugOnFailure.Should().BeTrue();
    }

    [Fact]
    public async Task GetDigestChatIdsAsync_ListsOnlyChatsWithTheDigestOn()
    {
        await _sut.SaveAsync(-100123, ChatSettings.Default with { WeeklyDigest = true }, CancellationToken.None);
        await _sut.SaveAsync(-100999, ChatSettings.Default, CancellationToken.None);

        (await _sut.GetDigestChatIdsAsync(CancellationToken.None)).Should().Equal(-100123L);
    }

    [Fact]
    public async Task SaveAsync_DigestTurnedOff_ForgetsTheRawChatId()
    {
        await _sut.SaveAsync(-100123, ChatSettings.Default with { WeeklyDigest = true }, CancellationToken.None);

        await _sut.SaveAsync(-100123, ChatSettings.Default, CancellationToken.None);

        (await _sut.GetDigestChatIdsAsync(CancellationToken.None)).Should().BeEmpty();
        (await _sut.GetAsync(-100123, CancellationToken.None)).WeeklyDigest.Should().BeFalse();
    }

    [Fact]
    public async Task SaveAsync_Twice_KeepsTheLatest()
    {
        await _sut.SaveAsync(-100123, new ChatSettings(CaptionMode.Brief, CaptionMode.Brief), CancellationToken.None);
        await _sut.SaveAsync(-100123, new ChatSettings(CaptionMode.Full, CaptionMode.Full), CancellationToken.None);

        (await _sut.GetAsync(-100123, CancellationToken.None))
            .Should().Be(new ChatSettings(CaptionMode.Full, CaptionMode.Full));
    }

    [Fact]
    public async Task SaveAsync_OneChat_LeavesOtherChatsOnDefault()
    {
        await _sut.SaveAsync(-100123, new ChatSettings(CaptionMode.Full, CaptionMode.Brief), CancellationToken.None);

        (await _sut.GetAsync(-100999, CancellationToken.None)).Should().Be(ChatSettings.Default);
    }

    [Fact]
    public async Task GetAsync_DatabaseUnreadable_FallsBackToDefault()
    {
        SqliteConnection.ClearAllPools();
        File.WriteAllText(_dbPath, "not a database");

        var settings = await _sut.GetAsync(-100123, CancellationToken.None);

        settings.Should().Be(ChatSettings.Default);
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
