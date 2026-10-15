namespace LeBot.Infrastructure.Configuration;

/// <summary>
/// The bot's storage chat (<c>Storage</c> section): a private channel the bot uploads every post's media
/// to once, then sends anywhere by Telegram file id. It backs both the repost cache and inline mode.
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>
    /// The channel's id — deployment identity, so user-secrets or <c>appsettings.Local.json</c> only. Unset,
    /// the cache falls back to the disk store and inline mode can't answer with media.
    /// </summary>
    public long? ChatId { get; init; }
}
