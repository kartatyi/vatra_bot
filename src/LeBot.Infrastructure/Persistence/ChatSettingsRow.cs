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

    public DateTimeOffset UpdatedAt { get; set; }
}
