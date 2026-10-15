namespace LeBot.Infrastructure.Persistence;

/// <summary>
/// One link one chat has seen. The link is stored as the media cache's key digest, never as a URL:
/// telling a repeat from a first needs only equality.
/// </summary>
internal sealed class SeenLinkRow
{
    public required string ChatHash { get; init; }

    public required string LinkKey { get; init; }

    public required DateTimeOffset FirstSeenAt { get; init; }
}
