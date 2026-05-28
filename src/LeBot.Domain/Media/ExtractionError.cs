namespace LeBot.Domain.Media;

/// <summary>
/// Reasons a platform extractor could fail to produce a <see cref="MediaPayload"/>.
/// Pattern-match on the concrete variant to decide how to log or surface the failure.
/// </summary>
public abstract record ExtractionError(string Reason)
{
    /// <summary>The URL's host isn't claimed by any registered extractor.</summary>
    public sealed record UnsupportedPlatform(Uri Url)
        : ExtractionError($"No extractor handles {Url.Host}.");

    /// <summary>The content exists in principle but the platform refused (private, deleted, age-restricted, geo-blocked).</summary>
    public sealed record ContentUnavailable(Uri Url, string Detail)
        : ExtractionError($"Content at {Url} is unavailable: {Detail}");

    /// <summary>HTTP / DNS / TLS layer failure reaching the platform.</summary>
    public sealed record NetworkFailure(Uri Url, string Detail)
        : ExtractionError($"Network failure fetching {Url}: {Detail}");

    /// <summary>The extractor tool (e.g. yt-dlp) ran but reported an error.</summary>
    public sealed record ToolFailure(string Detail)
        : ExtractionError($"Media extraction tool failed: {Detail}");
}
