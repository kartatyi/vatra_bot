using LeBot.Domain.Settings;

namespace LeBot.Infrastructure.Persistence;

/// <summary>
/// Storage shape of one chat's <see cref="ChatSettings"/>. Keyed by the same pseudonym the repost journal
/// uses (<see cref="Application.Telemetry.ChatHasher"/>), so the database never holds a raw chat id.
/// </summary>
internal sealed class ChatSettingsRow
{
    public required string ChatHash { get; init; }

    public CaptionMode VideoCaption { get; set; }

    public CaptionMode PostCaption { get; set; }

    public bool OfferFullText { get; set; }

    public bool ShrugOnFailure { get; set; }

    public bool WeeklyDigest { get; set; }

    /// <summary>
    /// Stored the negative way round — "stats off" — so every chat that existed before this column reads
    /// as the default, stats on, with no data migration.
    /// </summary>
    public bool StatsOff { get; set; }

    /// <summary>
    /// The raw chat id — held only while <see cref="WeeklyDigest"/> is on, because a scheduled post has to
    /// know where to go and a pseudonym can't be sent to. Cleared the moment the digest is turned off.
    /// </summary>
    public long? ChatId { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
