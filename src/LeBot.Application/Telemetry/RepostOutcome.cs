namespace LeBot.Application.Telemetry;

/// <summary>
/// What ultimately happened to one source URL the bot tried to process. One <see cref="RepostEvent"/>
/// is journalled per extractor attempt that reaches a terminal state, so a single URL can produce a
/// <see cref="Failure"/> (first extractor) followed by a <see cref="MediaRepost"/> (the next one that
/// saved it) — the dashboard aggregates these into per-platform success rates.
/// </summary>
public enum RepostOutcome
{
    /// <summary>Media was extracted and sent back to the chat.</summary>
    MediaRepost,

    /// <summary>No media, but the post's title/description was sent as a text reply.</summary>
    TextFallback,

    /// <summary>An extractor that claimed the URL reported a hard error (the "why it breaks" signal).</summary>
    Failure,

    /// <summary>An extractor claimed the URL but produced an empty payload — no media, no text.</summary>
    NothingExtracted,

    /// <summary>Extractors ran but none claimed the URL as theirs (all returned UnsupportedPlatform).</summary>
    NoExtractor,
}
