using LeBot.Application.Ports;
using LeBot.Application.Telemetry;
using LeBot.Domain.Common;
using LeBot.Domain.Media;

namespace LeBot.Application.Tests.Telemetry;

public class RepostJournalTests
{
    private static readonly DateTimeOffset Instant = new(2026, 6, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly Uri Url = new("https://tiktok.com/@a/video/1");

    private readonly IRepostEventStore _store = Substitute.For<IRepostEventStore>();
    private readonly IAppVersion _appVersion = Substitute.For<IAppVersion>();
    private readonly FakeTimeProvider _time = new(Instant);

    private RepostJournal CreateSut()
    {
        _appVersion.Current.Returns(new ReleaseVersion(1, 2, 3));
        return new RepostJournal(_store, _appVersion, _time);
    }

    [Fact]
    public async Task RecordMediaRepostAsync_StampsClockVersionAndHashedChat()
    {
        await CreateSut().RecordMediaRepostAsync(
            Url, "YtDlpPlatformExtractor", mediaCount: 2, mediaBytes: 2048,
            elapsed: TimeSpan.FromMilliseconds(1500), chatId: -100123, CancellationToken.None);

        await _store.Received(1).AppendAsync(
            Arg.Is<RepostEvent>(e =>
                e.Outcome == RepostOutcome.MediaRepost
                && e.Host == "tiktok.com"
                && e.Url == Url.ToString()
                && e.Extractor == "YtDlpPlatformExtractor"
                && e.MediaCount == 2
                && e.MediaBytes == 2048
                && e.ElapsedMs == 1500
                && e.OccurredAt == Instant
                && e.BotVersion == "1.2.3"
                && e.ChatHash == ChatHasher.Of(-100123)
                && e.ErrorVariant == null
                && e.ErrorReason == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordFailureAsync_MapsErrorVariantAndReason()
    {
        var error = new ExtractionError.NetworkFailure(Url, "boom");

        await CreateSut().RecordFailureAsync(
            Url, "ThreadsEmbedExtractor", error,
            elapsed: TimeSpan.FromMilliseconds(200), chatId: 5, CancellationToken.None);

        await _store.Received(1).AppendAsync(
            Arg.Is<RepostEvent>(e =>
                e.Outcome == RepostOutcome.Failure
                && e.ErrorVariant == "NetworkFailure"
                && e.ErrorReason == error.Reason
                && e.Extractor == "ThreadsEmbedExtractor"
                && e.MediaCount == 0
                && e.MediaBytes == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordTextFallbackAsync_RecordsOutcomeAndProducingExtractor()
    {
        await CreateSut().RecordTextFallbackAsync(
            Url, "InstagramEmbedExtractor",
            elapsed: TimeSpan.FromMilliseconds(80), chatId: 7, CancellationToken.None);

        await _store.Received(1).AppendAsync(
            Arg.Is<RepostEvent>(e =>
                e.Outcome == RepostOutcome.TextFallback
                && e.Extractor == "InstagramEmbedExtractor"
                && e.MediaCount == 0
                && e.MediaBytes == null
                && e.ErrorVariant == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordNothingExtractedAsync_RecordsOutcome()
    {
        await CreateSut().RecordNothingExtractedAsync(
            Url, "YtDlpPlatformExtractor",
            elapsed: TimeSpan.FromMilliseconds(50), chatId: 7, CancellationToken.None);

        await _store.Received(1).AppendAsync(
            Arg.Is<RepostEvent>(e =>
                e.Outcome == RepostOutcome.NothingExtracted
                && e.Extractor == "YtDlpPlatformExtractor"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordNoExtractorAsync_RecordsOutcomeWithNoExtractor()
    {
        await CreateSut().RecordNoExtractorAsync(
            Url, elapsed: TimeSpan.FromMilliseconds(1), chatId: 7, CancellationToken.None);

        await _store.Received(1).AppendAsync(
            Arg.Is<RepostEvent>(e =>
                e.Outcome == RepostOutcome.NoExtractor
                && e.Extractor == null),
            Arg.Any<CancellationToken>());
    }
}
