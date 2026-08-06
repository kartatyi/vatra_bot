namespace LeBot.Application.Telemetry;

/// <summary>
/// One durable record of the bot handling a single source URL — the row behind every dashboard
/// number. Unlike the in-memory <see cref="Metrics.RepostMetrics"/> counters (which reset on restart),
/// these are persisted so history survives restarts and self-updates, which is what makes "show me the
/// links that broke last week" and "did failures spike after vX" answerable.
/// </summary>
/// <param name="OccurredAt">When the attempt reached its terminal state (UTC, stamped from the injected clock).</param>
/// <param name="Host">Lower-cased host of the source URL (e.g. <c>tiktok.com</c>) — the grouping key for "top platforms".</param>
/// <param name="Url">The full source URL, so a failing link can be retried by hand.</param>
/// <param name="Outcome">What happened — see <see cref="RepostOutcome"/>.</param>
/// <param name="Extractor">The extractor that produced this outcome (its type name), or null when no extractor claimed the URL.</param>
/// <param name="ErrorVariant">For failures, the <see cref="Domain.Media.ExtractionError"/> variant name (e.g. <c>ContentUnavailable</c>); null otherwise.</param>
/// <param name="ErrorReason">For failures, the human-readable reason (includes the yt-dlp detail); null otherwise.</param>
/// <param name="MediaCount">Number of media items sent; 0 for non-media outcomes.</param>
/// <param name="MediaBytes">Total bytes of the sent media when known; null when the extractor didn't report sizes.</param>
/// <param name="ElapsedMs">Wall-clock milliseconds spent on this URL up to the terminal state.</param>
/// <param name="BotVersion">The running build's version, so a regression can be tied to the release that introduced it.</param>
/// <param name="ChatHash">A stable pseudonym of the chat id (not the raw id) so the dashboard can count distinct chats and split stats per chat; see <see cref="ChatHasher"/> for its privacy limits.</param>
public sealed record RepostEvent(
    DateTimeOffset OccurredAt,
    string Host,
    string Url,
    RepostOutcome Outcome,
    string? Extractor,
    string? ErrorVariant,
    string? ErrorReason,
    int MediaCount,
    long? MediaBytes,
    long ElapsedMs,
    string BotVersion,
    string ChatHash);
