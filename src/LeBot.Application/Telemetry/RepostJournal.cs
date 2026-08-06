using LeBot.Application.Ports;
using LeBot.Domain.Media;

namespace LeBot.Application.Telemetry;

/// <summary>
/// Builds a fully-stamped <see cref="RepostEvent"/> from an outcome the handler observed and hands it
/// to the <see cref="IRepostEventStore"/>. Centralising construction here keeps the use-case free of
/// telemetry plumbing (clock, build version, chat hashing, error-variant mapping) and keeps every
/// stored row consistent.
/// </summary>
public sealed class RepostJournal(
    IRepostEventStore store,
    IAppVersion appVersion,
    TimeProvider timeProvider) : IRepostJournal
{
    public Task RecordMediaRepostAsync(Uri url, string extractor, int mediaCount, long? mediaBytes, TimeSpan elapsed, long chatId, CancellationToken cancellationToken) =>
        AppendAsync(url, RepostOutcome.MediaRepost, extractor, errorVariant: null, errorReason: null, mediaCount, mediaBytes, elapsed, chatId, cancellationToken);

    public Task RecordTextFallbackAsync(Uri url, string? extractor, TimeSpan elapsed, long chatId, CancellationToken cancellationToken) =>
        AppendAsync(url, RepostOutcome.TextFallback, extractor, errorVariant: null, errorReason: null, mediaCount: 0, mediaBytes: null, elapsed, chatId, cancellationToken);

    public Task RecordFailureAsync(Uri url, string extractor, ExtractionError extractionError, TimeSpan elapsed, long chatId, CancellationToken cancellationToken) =>
        AppendAsync(url, RepostOutcome.Failure, extractor, errorVariant: extractionError.GetType().Name, errorReason: extractionError.Reason, mediaCount: 0, mediaBytes: null, elapsed, chatId, cancellationToken);

    public Task RecordNothingExtractedAsync(Uri url, string extractor, TimeSpan elapsed, long chatId, CancellationToken cancellationToken) =>
        AppendAsync(url, RepostOutcome.NothingExtracted, extractor, errorVariant: null, errorReason: null, mediaCount: 0, mediaBytes: null, elapsed, chatId, cancellationToken);

    public Task RecordNoExtractorAsync(Uri url, TimeSpan elapsed, long chatId, CancellationToken cancellationToken) =>
        AppendAsync(url, RepostOutcome.NoExtractor, extractor: null, errorVariant: null, errorReason: null, mediaCount: 0, mediaBytes: null, elapsed, chatId, cancellationToken);

    private Task AppendAsync(
        Uri url,
        RepostOutcome outcome,
        string? extractor,
        string? errorVariant,
        string? errorReason,
        int mediaCount,
        long? mediaBytes,
        TimeSpan elapsed,
        long chatId,
        CancellationToken cancellationToken)
    {
        var repostEvent = new RepostEvent(
            OccurredAt: timeProvider.GetUtcNow(),
            Host: url.Host.ToLowerInvariant(),
            Url: url.ToString(),
            Outcome: outcome,
            Extractor: extractor,
            ErrorVariant: errorVariant,
            ErrorReason: errorReason,
            MediaCount: mediaCount,
            MediaBytes: mediaBytes,
            ElapsedMs: (long)elapsed.TotalMilliseconds,
            BotVersion: appVersion.Current.ToString(),
            ChatHash: ChatHasher.Of(chatId));

        return store.AppendAsync(repostEvent, cancellationToken);
    }
}
