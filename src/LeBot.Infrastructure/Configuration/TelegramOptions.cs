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
}
