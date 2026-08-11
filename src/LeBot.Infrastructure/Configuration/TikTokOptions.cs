namespace LeBot.Infrastructure.Configuration;

/// <summary>
/// Bound from the <c>TikTok</c> section of configuration. Governs the mirror fallback, which only
/// ever runs after yt-dlp has already failed on a TikTok link (see ADR 0008).
/// </summary>
public sealed class TikTokOptions
{
    public const string SectionName = "TikTok";

    /// <summary>
    /// Whether the mirror fallback may run at all. False restores the behaviour from before it
    /// existed: a TikTok link yt-dlp cannot serve yields nothing and the chat stays silent.
    /// </summary>
    public bool MirrorFallbackEnabled { get; init; } = true;

    /// <summary>
    /// Mirror endpoints, tried in order until one answers with a playable URL. Each is called with
    /// the source link as a <c>url</c> query parameter and must reply in the tikwm response shape
    /// (<c>code</c> / <c>data.play</c> / <c>data.images</c>). A list rather than a single value so
    /// one mirror going down doesn't take the fallback with it.
    /// </summary>
    public IReadOnlyList<string> MirrorEndpoints { get; init; } = ["https://www.tikwm.com/api/"];

    /// <summary>
    /// How long a single mirror gets to answer before we move to the next one. Deliberately short:
    /// this runs after yt-dlp already spent its own time failing, and the chat is waiting.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 20;
}
