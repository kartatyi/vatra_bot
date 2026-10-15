namespace LeBot.Infrastructure.MediaCache;

/// <summary>
/// One post kept by <see cref="TelegramMediaCache"/>: the link's cache key, and the post as a JSON
/// <see cref="StoredPostDocument"/> — its text and the Telegram file ids of its media. No bytes are stored.
/// </summary>
internal sealed class StoredPostRow
{
    public required string LinkKey { get; init; }

    public required string Document { get; set; }

    public required DateTimeOffset StoredAt { get; set; }
}
