using LeBot.Domain.Media;

namespace LeBot.Application.Telemetry;

/// <summary>
/// The handler-facing recorder for durable repost telemetry. Mirrors <see cref="Metrics.RepostMetrics"/>
/// one method per outcome, so the call sites in the use-case read the same way — but where the metrics
/// bump in-memory counters, the journal stamps a full <see cref="RepostEvent"/> (clock, version, hashed
/// chat) and persists it via <see cref="IRepostEventStore"/>. The handler measures and passes
/// <c>elapsed</c>; the journal owns everything else about turning an outcome into a stored row.
/// </summary>
public interface IRepostJournal
{
    /// <summary>Media was extracted and sent.</summary>
    Task RecordMediaRepostAsync(Uri url, string extractor, int mediaCount, long? mediaBytes, TimeSpan elapsed, long chatId, CancellationToken cancellationToken);

    /// <summary>No media, but the post's text was sent as a fallback reply.</summary>
    Task RecordTextFallbackAsync(Uri url, string? extractor, TimeSpan elapsed, long chatId, CancellationToken cancellationToken);

    /// <summary>An extractor that claimed the URL failed hard; <paramref name="extractionError"/> carries the why.</summary>
    Task RecordFailureAsync(Uri url, string extractor, ExtractionError extractionError, TimeSpan elapsed, long chatId, CancellationToken cancellationToken);

    /// <summary>An extractor claimed the URL but produced an empty payload.</summary>
    Task RecordNothingExtractedAsync(Uri url, string extractor, TimeSpan elapsed, long chatId, CancellationToken cancellationToken);

    /// <summary>Extractors ran but none claimed the URL as theirs.</summary>
    Task RecordNoExtractorAsync(Uri url, TimeSpan elapsed, long chatId, CancellationToken cancellationToken);
}
