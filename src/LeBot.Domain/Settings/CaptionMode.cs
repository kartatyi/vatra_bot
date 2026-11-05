namespace LeBot.Domain.Settings;

/// <summary>How much of a post's text travels with the repost.</summary>
public enum CaptionMode
{
    /// <summary>Media only — no caption at all.</summary>
    Off,

    /// <summary>The text with hashtags and @mentions stripped, cut to a few lines (see <see cref="CaptionShortener"/>).</summary>
    Brief,

    /// <summary>The text as the platform gave it, up to Telegram's own limit.</summary>
    Full,
}
