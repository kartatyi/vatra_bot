using LeBot.Application.UseCases.AnswerInlineQuery;
using LeBot.Domain.Media;
using Telegram.Bot.Types.InlineQueryResults;
using Telegram.Bot.Types.ReplyMarkups;

namespace LeBot.Infrastructure.Telegram;

/// <summary>
/// Turns an <see cref="InlineAnswer"/> into the results Telegram shows above the keyboard, and how long
/// Telegram may cache them. Pure, so the copy and the shapes are testable without Telegram.
/// </summary>
internal static class InlineResults
{
    public const string PendingTitle = "⏳ Media — appears in a moment";
    public const string PendingWithTextTitle = "⏳ Media + text — appears in a moment";
    public const string LoadingText = "⏳ Loading the media…";
    public const string OriginalButton = "🔗 Original";
    public const string FailedTitle = "🤷 Couldn't get media from this link";
    public const string WithoutTextDescription = "Without the post's text";

    internal const int CaptionLimit = 1024;
    internal const int TextLimit = 4096;

    private const string PendingBareId = "pending:bare";
    private const string PendingTextId = "pending:text";
    private const int PreviewLength = 80;

    /// <summary>
    /// The results for <paramref name="answer"/>, and how long Telegram may cache them. Only a text-only post
    /// is cached: every media choice is a placeholder the chosen-result path fills in, and "not ready" must
    /// let the next keystroke reach the finished preparation.
    /// </summary>
    public static (IReadOnlyList<InlineQueryResult> Results, int CacheSeconds) For(InlineAnswer answer) => answer switch
    {
        InlineAnswer.Ready { Post.HasMedia: true } ready => (MediaResults(ready.Post, ready.Url), 0),
        InlineAnswer.Ready ready => (TextResults(ready.Post), 300),
        InlineAnswer.NotReady notReady => (PendingResults(notReady.Url), 0),
        InlineAnswer.Failed failed => ([LinkArticle("failed", FailedTitle, failed.Url)], 0),
        _ => ([], 0),
    };

    /// <summary>
    /// Whether <paramref name="resultId"/> is one of the "loading" placeholders, and which. Anything else —
    /// a ready post, the failure notice — needs no follow-up once chosen.
    /// </summary>
    public static bool TryParsePending(string resultId, out bool withText)
    {
        withText = resultId == PendingTextId;
        return resultId is PendingBareId or PendingTextId;
    }

    /// <summary>The "🔗 Original" button under a placeholder: somewhere to go while it loads, or if it never does.</summary>
    public static InlineKeyboardMarkup OriginalLink(Uri url) => new(InlineKeyboardButton.WithUrl(OriginalButton, url.AbsoluteUri));

    /// <summary>
    /// Two placeholders mirroring the ready choices. The keyboard isn't decoration: Telegram only reports
    /// which inline message was sent — the handle the bot needs to edit it later — when it carries one.
    /// </summary>
    private static List<InlineQueryResult> PendingResults(Uri url)
    {
        var content = new InputTextMessageContent($"{LoadingText}\n{url.AbsoluteUri}");
        return
        [
            new InlineQueryResultArticle(PendingBareId, PendingTitle, content) { Description = url.AbsoluteUri, ReplyMarkup = OriginalLink(url) },
            new InlineQueryResultArticle(PendingTextId, PendingWithTextTitle, content) { Description = url.AbsoluteUri, ReplyMarkup = OriginalLink(url) },
        ];
    }

    /// <summary>
    /// A media post's choices, as titled placeholders rather than media results: clients show photo and video
    /// results as bare thumbnails — no title, no description — so "with text" and "without" can't be told
    /// apart. Choosing one sends the placeholder, and the chosen-result path swaps the stored media into it
    /// straight away: it's already in the storage chat. Two choices at most — the media, or the media with
    /// the post's text.
    /// </summary>
    private static List<InlineQueryResult> MediaResults(PreparedPost post, Uri url)
    {
        var (label, labelWithText) = post.Kind switch
        {
            MediaKind.Photo => ("🖼 Photo", "🖼 Photo + text"),
            MediaKind.Animation => ("🎞 GIF", "🎞 GIF + text"),
            MediaKind.Audio => ("🎵 Audio", "🎵 Audio + text"),
            _ => ("📹 Video", "📹 Video + text"),
        };

        var content = new InputTextMessageContent($"{LoadingText}\n{url.AbsoluteUri}");
        List<InlineQueryResult> results =
        [
            new InlineQueryResultArticle(PendingBareId, label, content) { Description = WithoutTextDescription, ReplyMarkup = OriginalLink(url) },
        ];
        if (post.Body is { } body)
        {
            results.Add(new InlineQueryResultArticle(PendingTextId, labelWithText, content) { Description = Preview(body), ReplyMarkup = OriginalLink(url) });
        }

        return results;
    }

    private static List<InlineQueryResult> TextResults(PreparedPost post) =>
        post.Body is { } body
            ? [new InlineQueryResultArticle("text", "📝 Text", new InputTextMessageContent(Fit(body, TextLimit))) { Description = Preview(body) }]
            : [];

    /// <summary>Choosing it sends the link itself — in a chat with the bot, that still gets reposted.</summary>
    private static InlineQueryResultArticle LinkArticle(string id, string title, Uri url) =>
        new(id, title, new InputTextMessageContent(url.AbsoluteUri)) { Description = url.AbsoluteUri };

    private static string Preview(string body)
    {
        var oneLine = body.ReplaceLineEndings(" ");
        return oneLine.Length > PreviewLength ? oneLine[..(PreviewLength - 1)] + "…" : oneLine;
    }

    internal static string Fit(string text, int limit) =>
        text.Length > limit ? text[..(limit - 1)] + "…" : text;
}
