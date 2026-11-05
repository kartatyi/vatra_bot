namespace LeBot.Application.Ports;

/// <summary>
/// Holds the text a repost's caption settings held back, keyed by the repost message, until someone
/// presses its "show text" button. Entries expire after a few days; a press after that finds nothing.
/// </summary>
public interface IRevealableTextStore
{
    /// <summary>
    /// Remembers <paramref name="text"/> for the repost <paramref name="messageId"/> in <paramref name="chatId"/>.
    /// <b>Best-effort:</b> a storage failure is logged, not thrown — the repost itself already went out,
    /// and the worst outcome is a button that answers "no longer available".
    /// </summary>
    Task SaveAsync(long chatId, int messageId, string text, CancellationToken cancellationToken);

    /// <summary>
    /// Returns and forgets the text held for that repost, or <see langword="null"/> when there is none —
    /// never stored, already shown, or expired.
    /// </summary>
    Task<string?> TakeAsync(long chatId, int messageId, CancellationToken cancellationToken);
}
