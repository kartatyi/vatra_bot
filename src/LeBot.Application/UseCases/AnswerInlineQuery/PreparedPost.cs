using LeBot.Domain.Media;

namespace LeBot.Application.UseCases.AnswerInlineQuery;

/// <summary>
/// A link turned into something an inline answer can send at once: a file already uploaded to Telegram,
/// referenced by its <c>file_id</c>, plus the post's text — or just the text, when the post had no media.
/// </summary>
/// <param name="Kind">What the uploaded file is; null for a text-only post.</param>
/// <param name="FileId">Telegram's id for the uploaded file; null for a text-only post.</param>
/// <param name="Body">The post's text as a repost would carry it (see <see cref="MediaPayload.Body"/>); may be null.</param>
public sealed record PreparedPost(MediaKind? Kind, string? FileId, string? Body)
{
    /// <summary>True when there's a file to send, not just text.</summary>
    public bool HasMedia => FileId is not null;
}
