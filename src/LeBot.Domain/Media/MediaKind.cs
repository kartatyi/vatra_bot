namespace LeBot.Domain.Media;

/// <summary>
/// The kind of media the bot is about to send back to Telegram.
/// Mirrors the Telegram Bot API distinctions because that's what dictates which send method we call.
/// </summary>
public enum MediaKind
{
    Video,
    Photo,
    Animation,
    Audio,
}
