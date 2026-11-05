namespace LeBot.Infrastructure.Configuration;

/// <summary>
/// Bound from the <c>Threads</c> section. Controls the headless-browser fallback that reads a post's
/// payload when a plain HTTP fetch comes back with the logged-out shell instead (see ADR 0008).
/// </summary>
public sealed class ThreadsOptions
{
    public const string SectionName = "Threads";

    /// <summary>
    /// Master switch for the headless-browser fallback. When false — or when no Chromium browser is
    /// found on the host — a post whose payload the plain fetch missed falls back to the og:image
    /// card the embed extractor produces.
    /// </summary>
    public bool BrowserFallbackEnabled { get; init; } = true;

    /// <summary>
    /// Explicit path to a Chromium-family browser (chrome.exe / msedge.exe). Leave null to
    /// auto-detect the system Chrome first, then Edge.
    /// </summary>
    public string? BrowserPath { get; init; }

    /// <summary>
    /// How long to give the browser to launch, load the page, and render the post's payload before
    /// giving up and falling back to the card.
    /// </summary>
    public int PageTimeoutSeconds { get; init; } = 25;
}
