using LeBot.Application.UseCases.AnswerInlineQuery;

namespace LeBot.Application.Ports;

/// <summary>
/// Links already turned into a <see cref="PreparedPost"/>, so a repeat inline query is answered without
/// fetching or uploading anything. Keyed like the media cache: share links differing only by tracking
/// params are one link.
/// </summary>
public interface IPreparedPostStore
{
    /// <summary>The prepared post for <paramref name="url"/>, or <see langword="null"/>. Never throws on a storage failure.</summary>
    Task<PreparedPost?> TryGetAsync(Uri url, CancellationToken cancellationToken);

    /// <summary>Remembers <paramref name="post"/> for <paramref name="url"/>. Best-effort: a failure is logged, not thrown.</summary>
    Task SaveAsync(Uri url, PreparedPost post, CancellationToken cancellationToken);
}
