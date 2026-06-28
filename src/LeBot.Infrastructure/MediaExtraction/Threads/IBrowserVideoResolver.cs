namespace LeBot.Infrastructure.MediaExtraction.Threads;

/// <summary>
/// Drives a headless browser to recover the direct media URL a page only exposes after its
/// client-side JavaScript has run. Threads is the sole caller today: it renders post video
/// client-side, so the playable URL is absent from the server HTML (see ADR 0006).
/// </summary>
internal interface IBrowserVideoResolver
{
    /// <summary>
    /// Loads <paramref name="pageUrl"/> in a headless browser and returns the first rendered
    /// &lt;video&gt; element's direct source URL, or <c>null</c> when the page carries no playable
    /// video (a photo / text-only post), the browser is unavailable, or resolution times out.
    /// Never throws for those expected outcomes — a <c>null</c> tells the caller to fall back.
    /// </summary>
    Task<string?> ResolveVideoUrlAsync(Uri pageUrl, CancellationToken cancellationToken);
}
