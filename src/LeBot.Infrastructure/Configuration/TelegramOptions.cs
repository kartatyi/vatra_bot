namespace LeBot.Infrastructure.Configuration;

/// <summary>
/// Bound from the <c>Telegram</c> section of configuration.
/// The token is supplied via <c>dotnet user-secrets</c> in dev or the
/// <c>Telegram__BotToken</c> environment variable in prod — never committed.
/// </summary>
public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public string BotToken { get; init; } = string.Empty;

    /// <summary>Seconds the bot waits on a single long-poll request before reconnecting.</summary>
    public int PollingTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// How many updates are handled at once, across all chats. A chat's own updates run one at a time, so
    /// this is how many chats are served side by side — each possibly downloading, transcoding and uploading
    /// up to <c>YtDlp:MaxFileSizeMb</c> of media. Values below 1 count as 1.
    /// </summary>
    /// <remarks>
    /// 8 by default: above the 4 yt-dlp processes YoutubeDLSharp runs at once, so a cache hit, a command or
    /// a button press doesn't queue behind four downloads; small enough for the one home box the bot runs
    /// on. A stuck chat costs one slot of the eight, not the whole bot.
    /// </remarks>
    public int MaxConcurrentUpdates { get; init; } = 8;
}
