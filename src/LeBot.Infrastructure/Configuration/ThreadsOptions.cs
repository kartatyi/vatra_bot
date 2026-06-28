namespace LeBot.Infrastructure.Configuration;

/// <summary>
/// Bound from the <c>Threads</c> section. Controls the headless-browser path that pulls the real
/// video URL off a Threads post: Threads renders post media client-side, so the playable URL only
/// exists after the page's JavaScript runs and never appears in the server HTML (see ADR 0006).
/// </summary>
public sealed class ThreadsOptions
{
    public const string SectionName = "Threads";

    /// <summary>
    /// Master switch for the headless-browser video extractor. When false — or when no Chromium
    /// browser is found on the host — Threads video posts fall back to the og:image thumbnail
    /// the embed extractor already produces, i.e. today's behaviour.
    /// </summary>
    public bool VideoExtractionEnabled { get; init; } = true;

    /// <summary>
    /// Explicit path to a Chromium-family browser (chrome.exe / msedge.exe). Leave null to
    /// auto-detect the system Chrome first, then Edge.
    /// </summary>
    public string? BrowserPath { get; init; }

    /// <summary>
    /// How long to give the page to launch the browser, load, and render its &lt;video&gt; element
    /// before giving up and falling back to the thumbnail.
    /// </summary>
    public int PageTimeoutSeconds { get; init; } = 25;
}
