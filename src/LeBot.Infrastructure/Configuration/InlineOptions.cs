namespace LeBot.Infrastructure.Configuration;

/// <summary>
/// Inline mode (<c>@bot &lt;link&gt;</c> in any chat). Inline results can only point at files Telegram
/// already holds, so the bot first uploads each post to a storage chat it administers.
/// </summary>
public sealed class InlineOptions
{
    public const string SectionName = "Inline";

    /// <summary>
    /// Id of the private channel the bot uploads to — deployment identity, so set it in user-secrets or
    /// the environment, never in a committed file. Unset, inline answers carry text posts only and every
    /// media post answers "couldn't get media".
    /// </summary>
    public long? StorageChatId { get; init; }
}
