using LeBot.Application.UseCases.AnswerInlineQuery;
using LeBot.Domain.Media;
using Telegram.Bot.Types.InlineQueryResults;

namespace LeBot.Infrastructure.Telegram;

/// <summary>
/// Turns an <see cref="InlineAnswer"/> into the results Telegram shows above the keyboard, and how long
/// Telegram may cache them. Pure, so the copy and the shapes are testable without Telegram.
/// </summary>
internal static class InlineResults
{
    public const string NotReadyTitle = "⏳ Not ready yet — send the link instead";
    public const string FailedTitle = "🤷 Couldn't get media from this link";

    private const int CaptionLimit = 1024;
    private const int TextLimit = 4096;
    private const int PreviewLength = 80;

    /// <summary>
    /// Ready posts may be cached by Telegram for a while; "not ready" must not be, or the next keystroke
    /// would be answered from Telegram's cache instead of reaching the finished preparation.
    /// </summary>
    public static (IReadOnlyList<InlineQueryResult> Results, int CacheSeconds) For(InlineAnswer answer) => answer switch
    {
        InlineAnswer.Ready { Post.HasMedia: true } ready => (MediaResults(ready.Post), 300),
        InlineAnswer.Ready ready => (TextResults(ready.Post), 300),
        InlineAnswer.NotReady notReady => ([LinkArticle("wait", NotReadyTitle, notReady.Url)], 0),
        InlineAnswer.Failed failed => ([LinkArticle("failed", FailedTitle, failed.Url)], 0),
        _ => ([], 0),
    };

    /// <summary>The bare file, and — when the post has text — the file with the text as its caption.</summary>
    private static List<InlineQueryResult> MediaResults(PreparedPost post)
    {
        // HasMedia guarantees both; the pattern just lets the compiler see it.
        if (post is not { Kind: { } kind, FileId: { } fileId })
        {
            return [];
        }

        var (label, labelWithText) = kind == MediaKind.Photo
            ? ("🖼 Photo", "🖼 Photo + text")
            : ("📹 Video", "📹 Video + text");

        List<InlineQueryResult> results = [Media("bare", kind, fileId, label, caption: null, description: null)];
        if (post.Body is { } body)
        {
            results.Add(Media("text", kind, fileId, labelWithText, Fit(body, CaptionLimit), Preview(body)));
        }

        return results;
    }

    private static List<InlineQueryResult> TextResults(PreparedPost post) =>
        post.Body is { } body
            ? [new InlineQueryResultArticle("text", "📝 Text", new InputTextMessageContent(Fit(body, TextLimit))) { Description = Preview(body) }]
            : [];

    private static InlineQueryResult Media(string id, MediaKind kind, string fileId, string title, string? caption, string? description) => kind switch
    {
        MediaKind.Photo => new InlineQueryResultCachedPhoto(id, fileId) { Title = title, Caption = caption, Description = description },
        MediaKind.Animation => new InlineQueryResultCachedMpeg4Gif(id, fileId) { Title = title, Caption = caption },
        MediaKind.Audio => new InlineQueryResultCachedAudio(id, fileId) { Caption = caption },
        _ => new InlineQueryResultCachedVideo(id, fileId, title) { Caption = caption, Description = description },
    };

    /// <summary>Choosing it sends the link itself — in a chat with the bot, that still gets reposted.</summary>
    private static InlineQueryResultArticle LinkArticle(string id, string title, Uri url) =>
        new(id, title, new InputTextMessageContent(url.AbsoluteUri)) { Description = url.AbsoluteUri };

    private static string Preview(string body)
    {
        var oneLine = body.ReplaceLineEndings(" ");
        return oneLine.Length > PreviewLength ? oneLine[..(PreviewLength - 1)] + "…" : oneLine;
    }

    private static string Fit(string text, int limit) =>
        text.Length > limit ? text[..(limit - 1)] + "…" : text;
}
