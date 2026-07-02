using LeBot.Application.Telemetry;
using LeBot.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LeBot.Infrastructure.Tests.Persistence;

/// <summary>
/// Exercises the read-side query API against a real SQLite file — which also proves the EF LINQ actually
/// translates (GROUP BY, conditional SUM, MIN/MAX over the DateTimeOffset window, OFFSET for the p95) on
/// the SQLite provider rather than only against an in-memory store.
/// </summary>
public sealed class SqliteRepostEventStoreQueryTests : IDisposable
{
    private static readonly DateTimeOffset BaseInstant = new(2026, 6, 28, 10, 0, 0, TimeSpan.Zero);

    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"lebot-query-{Path.GetRandomFileName()}.db");

    private readonly DbContextOptions<LeBotDbContext> _options;
    private readonly SqliteRepostEventStore _sut;

    public SqliteRepostEventStoreQueryTests()
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

    [Fact]
    public async Task GetStatsAsync_RollsUpEveryOutcomeAndCountsDistinctChats()
    {
        await SeedAsync(
            Event(RepostOutcome.MediaRepost, chatHash: "chat-a"),
            Event(RepostOutcome.MediaRepost, chatHash: "chat-b"),
            Event(RepostOutcome.TextFallback, chatHash: "chat-a"),
            Event(RepostOutcome.Failure, chatHash: "chat-a"),
            Event(RepostOutcome.NothingExtracted, chatHash: "chat-a"),
            Event(RepostOutcome.NoExtractor, extractor: null, chatHash: "chat-a"));

        var stats = await _sut.GetStatsAsync(DateTimeOffset.MinValue, CancellationToken.None);

        stats.TotalProcessed.Should().Be(6);
        stats.MediaReposts.Should().Be(2);
        stats.TextFallbacks.Should().Be(1);
        stats.Failures.Should().Be(1);
        stats.NothingExtracted.Should().Be(1);
        stats.NoExtractor.Should().Be(1);
        stats.DistinctChats.Should().Be(2);
        stats.Successes.Should().Be(3);
        stats.SuccessRate.Should().Be(0.5);
    }

    [Fact]
    public async Task GetStatsAsync_StampsFirstAndLastEventInWindow()
    {
        await SeedAsync(
            Event(occurredAt: BaseInstant),
            Event(occurredAt: BaseInstant.AddHours(3)),
            Event(occurredAt: BaseInstant.AddHours(1)));

        var stats = await _sut.GetStatsAsync(DateTimeOffset.MinValue, CancellationToken.None);

        stats.FirstEventAt.Should().Be(BaseInstant);
        stats.LastEventAt.Should().Be(BaseInstant.AddHours(3));
    }

    [Fact]
    public async Task GetStatsAsync_OnlyCountsEventsAtOrAfterSince()
    {
        await SeedAsync(
            Event(occurredAt: BaseInstant.AddHours(-2)),
            Event(occurredAt: BaseInstant));

        var stats = await _sut.GetStatsAsync(BaseInstant.AddHours(-1), CancellationToken.None);

        stats.TotalProcessed.Should().Be(1);
    }

    [Fact]
    public async Task GetStatsAsync_EmptyWindow_ReturnsZeroSnapshot()
    {
        await SeedAsync(Event(occurredAt: BaseInstant.AddHours(-2)));

        var stats = await _sut.GetStatsAsync(BaseInstant, CancellationToken.None);

        stats.Should().Be(RepostStatsSnapshot.Empty);
    }

    [Fact]
    public async Task GetStatsAsync_FiltersRowStoredInLegacyDefaultDateTimeOffsetFormat()
    {
        // A row exactly as EF Core's default DateTimeOffset mapping wrote it before the value converter
        // existed: space separator, no 'T', "+00:00" offset. The window filter (which formats `since`
        // through the same converter) must still compare against it correctly.
        await using (var db = new LeBotDbContext(_options))
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO RepostEvents " +
                "(OccurredAt, Host, Url, Outcome, MediaCount, ElapsedMs, BotVersion, ChatHash) " +
                "VALUES ('2026-06-28 10:00:00+00:00', 'tiktok.com', 'https://tiktok.com/x', " +
                "'MediaRepost', 1, 100, '1.0.0', 'chat-a')");
        }

        var includesIt = await _sut.GetStatsAsync(BaseInstant.AddHours(-1), CancellationToken.None);
        var excludesIt = await _sut.GetStatsAsync(BaseInstant.AddHours(1), CancellationToken.None);

        includesIt.TotalProcessed.Should().Be(1);
        excludesIt.TotalProcessed.Should().Be(0);
    }

    [Fact]
    public async Task GetRecentFailuresAsync_ReturnsOnlyFailures_NewestFirst_HonoringLimit()
    {
        await SeedAsync(
            Event(RepostOutcome.Failure, occurredAt: BaseInstant.AddMinutes(1)),
            Event(RepostOutcome.Failure, occurredAt: BaseInstant.AddMinutes(2)),
            Event(RepostOutcome.Failure, occurredAt: BaseInstant.AddMinutes(3)),
            Event(RepostOutcome.MediaRepost, occurredAt: BaseInstant.AddMinutes(4)));

        var failures = await _sut.GetRecentFailuresAsync(limit: 2, CancellationToken.None);

        failures.Should().HaveCount(2);
        failures.Select(f => f.OccurredAt).Should().ContainInOrder(
            BaseInstant.AddMinutes(3), BaseInstant.AddMinutes(2));
    }

    [Fact]
    public async Task GetRecentFailuresAsync_ProjectsErrorDetail()
    {
        await SeedAsync(Event(
            RepostOutcome.Failure,
            host: "instagram.com",
            extractor: "InstagramApiExtractor",
            errorVariant: "ContentUnavailable",
            errorReason: "login required",
            url: "https://instagram.com/p/broken"));

        var failure = (await _sut.GetRecentFailuresAsync(limit: 5, CancellationToken.None)).Single();

        failure.Host.Should().Be("instagram.com");
        failure.Url.Should().Be("https://instagram.com/p/broken");
        failure.ErrorVariant.Should().Be("ContentUnavailable");
        failure.ErrorReason.Should().Be("login required");
        failure.Extractor.Should().Be("InstagramApiExtractor");
    }

    [Fact]
    public async Task GetTopHostsByVolumeAsync_OrdersByTotalDescending_HonoringLimit()
    {
        await SeedAsync(
            Event(host: "tiktok.com"),
            Event(host: "tiktok.com"),
            Event(host: "tiktok.com"),
            Event(host: "x.com"),
            Event(host: "x.com"),
            Event(host: "instagram.com"));

        var top = await _sut.GetTopHostsByVolumeAsync(limit: 2, DateTimeOffset.MinValue, CancellationToken.None);

        top.Select(h => h.Host).Should().ContainInOrder("tiktok.com", "x.com");
        top.Should().HaveCount(2);
        top[0].Total.Should().Be(3);
    }

    [Fact]
    public async Task GetTopHostsByFailureRateAsync_RanksByRate_ExcludingHostsBelowMinVolume()
    {
        await SeedAsync(
            // hostA: a single 1-of-1 failure (rate 1.0) — must be filtered out by minVolume.
            Event(RepostOutcome.Failure, host: "rare.com"),
            // hostB: 4 of 5 failed (rate 0.8).
            Event(RepostOutcome.Failure, host: "flaky.com"),
            Event(RepostOutcome.Failure, host: "flaky.com"),
            Event(RepostOutcome.Failure, host: "flaky.com"),
            Event(RepostOutcome.Failure, host: "flaky.com"),
            Event(RepostOutcome.MediaRepost, host: "flaky.com"),
            // hostC: 1 of 4 failed (rate 0.25).
            Event(RepostOutcome.Failure, host: "solid.com"),
            Event(RepostOutcome.MediaRepost, host: "solid.com"),
            Event(RepostOutcome.MediaRepost, host: "solid.com"),
            Event(RepostOutcome.MediaRepost, host: "solid.com"));

        var top = await _sut.GetTopHostsByFailureRateAsync(limit: 5, minVolume: 2, DateTimeOffset.MinValue, CancellationToken.None);

        top.Select(h => h.Host).Should().ContainInOrder("flaky.com", "solid.com");
        top.Should().NotContain(h => h.Host == "rare.com");
        top[0].FailureRate.Should().BeApproximately(0.8, 1e-9);
    }

    [Fact]
    public async Task GetExtractorStatsAsync_TalliesSuccessesAndFailures_ExcludingNullExtractor()
    {
        await SeedAsync(
            Event(RepostOutcome.MediaRepost, extractor: "YtDlpPlatformExtractor"),
            Event(RepostOutcome.MediaRepost, extractor: "YtDlpPlatformExtractor"),
            Event(RepostOutcome.Failure, extractor: "YtDlpPlatformExtractor"),
            Event(RepostOutcome.TextFallback, extractor: "ThreadsEmbedExtractor"),
            Event(RepostOutcome.NoExtractor, extractor: null));

        var stats = await _sut.GetExtractorStatsAsync(DateTimeOffset.MinValue, CancellationToken.None);

        stats.Should().HaveCount(2);
        var ytdlp = stats.Single(s => s.Extractor == "YtDlpPlatformExtractor");
        ytdlp.Total.Should().Be(3);
        ytdlp.Successes.Should().Be(2);
        ytdlp.Failures.Should().Be(1);
        stats[0].Extractor.Should().Be("YtDlpPlatformExtractor"); // busiest first
    }

    [Fact]
    public async Task GetLatencyAsync_ComputesAverageAndP95ByRank()
    {
        // Elapsed 100..2000 ms in 100 ms steps: mean 1050, p95 (rank 18 of 20) lands on the 1900 ms row.
        await SeedAsync(Enumerable.Range(1, 20)
            .Select(i => Event(elapsedMs: i * 100))
            .ToArray());

        var latency = await _sut.GetLatencyAsync(DateTimeOffset.MinValue, CancellationToken.None);

        latency.SampleCount.Should().Be(20);
        latency.AverageMs.Should().Be(1050d);
        latency.P95Ms.Should().Be(1900);
    }

    [Fact]
    public async Task GetLatencyAsync_EmptyWindow_ReturnsEmpty()
    {
        var latency = await _sut.GetLatencyAsync(BaseInstant, CancellationToken.None);

        latency.Should().Be(LatencySummary.Empty);
    }

    private async Task SeedAsync(params RepostEvent[] events)
    {
        await using var db = new LeBotDbContext(_options);
        db.RepostEvents.AddRange(events);
        await db.SaveChangesAsync();
    }

    private static RepostEvent Event(
        RepostOutcome outcome = RepostOutcome.MediaRepost,
        string host = "tiktok.com",
        string? extractor = "YtDlpPlatformExtractor",
        DateTimeOffset? occurredAt = null,
        long elapsedMs = 100,
        string chatHash = "chat-a",
        string? errorVariant = null,
        string? errorReason = null,
        string? url = null) =>
        new(
            OccurredAt: occurredAt ?? BaseInstant,
            Host: host,
            Url: url ?? $"https://{host}/p/1",
            Outcome: outcome,
            Extractor: extractor,
            ErrorVariant: errorVariant,
            ErrorReason: errorReason,
            MediaCount: outcome == RepostOutcome.MediaRepost ? 1 : 0,
            MediaBytes: outcome == RepostOutcome.MediaRepost ? 1024 : null,
            ElapsedMs: elapsedMs,
            BotVersion: "1.0.0",
            ChatHash: chatHash);

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
