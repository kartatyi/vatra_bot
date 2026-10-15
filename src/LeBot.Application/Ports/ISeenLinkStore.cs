namespace LeBot.Application.Ports;

/// <summary>Remembers which links each chat has already seen, so a repeat can be told apart from a first.</summary>
public interface ISeenLinkStore
{
    /// <summary>
    /// Records that <paramref name="chatId"/> saw <paramref name="url"/> and reports whether it had before.
    /// Two share links to the same post count as one link when they differ only by tracking noise.
    /// <b>Never throws on a storage failure</b> — it answers <see langword="false"/>: a missed repeat is
    /// harmless, a broken repost is not.
    /// </summary>
    Task<bool> MarkSeenAsync(long chatId, Uri url, CancellationToken cancellationToken);
}
