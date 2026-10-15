namespace LeBot.Infrastructure.Persistence;

/// <summary>
/// Storage shape of one held-back caption: the full text of a repost, waiting for its "show text"
/// button. Keyed by the chat pseudonym and the repost's message id.
/// </summary>
internal sealed class RevealableTextRow
{
    public required string ChatHash { get; init; }

    public required int MessageId { get; init; }

    public required string Text { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}
