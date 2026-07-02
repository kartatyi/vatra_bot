namespace LeBot.Application.Telemetry;

/// <summary>
/// One broken link, as the dashboard and the <c>/failures</c> command show it: the URL that failed, the
/// platform it was on, and the error the extractor reported — enough to retry it by hand or spot a
/// platform that started breaking. Projected from a <see cref="RepostEvent"/> with
/// <see cref="RepostOutcome.Failure"/>.
/// </summary>
/// <param name="OccurredAt">When the failure was recorded (UTC).</param>
/// <param name="Host">The platform host (e.g. <c>tiktok.com</c>).</param>
/// <param name="Url">The full source URL, so the link can be retried.</param>
/// <param name="ErrorVariant">The <see cref="Domain.Media.ExtractionError"/> variant name (e.g. <c>ContentUnavailable</c>).</param>
/// <param name="ErrorReason">The human-readable reason, including the yt-dlp detail when present.</param>
/// <param name="Extractor">The extractor that reported the failure.</param>
public sealed record RecentFailure(
    DateTimeOffset OccurredAt,
    string Host,
    string Url,
    string? ErrorVariant,
    string? ErrorReason,
    string? Extractor);
