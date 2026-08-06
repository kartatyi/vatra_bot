namespace LeBot.Infrastructure.MediaExtraction.Threads;

/// <summary>
/// Loads the JSON payload block a Threads page carries for one specific post, by letting a real
/// browser fetch the page. Only used when a plain HTTP fetch came back without it — Meta serves the
/// payload to requests it believes are browsers, and occasionally answers ours with the logged-out
/// shell instead.
/// </summary>
internal interface IBrowserPayloadLoader
{
    /// <summary>
    /// Returns the payload block describing <paramref name="shortcode"/>, or null when no browser is
    /// available, the page never rendered one, or anything went wrong (all expected failures are
    /// logged and swallowed — the caller falls back rather than failing).
    /// </summary>
    Task<string?> LoadPostPayloadAsync(Uri pageUrl, string shortcode, CancellationToken cancellationToken);
}
