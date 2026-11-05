namespace LeBot.Infrastructure.Persistence;

/// <summary>
/// One time a chat saw a link: who posted it and whether the chat had seen it before. The link is the
/// media cache's key digest, never a URL. Sightings answer "is this a repeat" (🥱 on the message) and
/// feed the ⚡ and 🥱 badges.
/// </summary>
internal sealed class LinkSightingRow
{
    public long Id { get; init; }

    public required string ChatHash { get; init; }

    public required string LinkKey { get; init; }

    /// <summary>Who posted it, when a person did; null for bots, anonymous admins and channel posts.</summary>
    public long? SenderId { get; init; }

    public required DateTimeOffset SeenAt { get; init; }

    public required bool IsRepeat { get; init; }
}
