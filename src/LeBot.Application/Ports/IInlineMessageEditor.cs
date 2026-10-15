using LeBot.Application.UseCases.AnswerInlineQuery;

namespace LeBot.Application.Ports;

/// <summary>
/// Rewrites a message someone sent through inline mode — the "loading" placeholder they picked while the
/// post was still being fetched — once the post is ready or known to be lost.
/// </summary>
public interface IInlineMessageEditor
{
    /// <summary>
    /// Turns the placeholder into <paramref name="post"/>: its media, captioned with the post's text when
    /// <paramref name="withText"/>, or the text alone for a post with no media — keeping a link to
    /// <paramref name="url"/>, since whoever receives it may want the original. Best-effort: a placeholder the
    /// sender already deleted is simply gone.
    /// </summary>
    Task ShowPostAsync(string inlineMessageId, PreparedPost post, bool withText, Uri url, string? languageCode, CancellationToken cancellationToken);

    /// <summary>
    /// Turns the placeholder into "couldn't get media", in the sender's <paramref name="languageCode"/>,
    /// keeping the link so it still leads somewhere.
    /// </summary>
    Task ShowFailureAsync(string inlineMessageId, Uri url, string? languageCode, CancellationToken cancellationToken);
}
