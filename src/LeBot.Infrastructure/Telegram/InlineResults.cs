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
    public const string TextOnlyTitle = "📝 Text only";

    internal const int CaptionLimit = 1024;
    internal const int TextLimit = 4096;

    private const string PendingBareId = "pending:bare";
    private const string PendingTextId = "pending:text";
    private const int PreviewLength = 80;

    /// <summary>
    /// Ready posts may be cached by Telegram for a while; "not ready" must not be, or the next keystroke
    /// would be answered from Telegram's cache instead of reaching the finished preparation.
    /// </summary>
    public static (IReadOnlyList<InlineQueryResult> Results, int CacheSeconds) For(InlineAnswer answer) => answer switch
    {
        // Photo choices are placeholders, and a placeholder is filled in by the chosen-result path —
        // a result Telegram served from its own cache wouldn't be, so they're not cached either.
        InlineAnswer.Ready { Post: { HasMedia: true, Kind: MediaKind.Photo } } ready => (PhotoResults(ready.Post, ready.Url), 0),
        InlineAnswer.Ready { Post.HasMedia: true } ready => (MediaResults(ready.Post), 300),
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
    /// A photo post's choices, as titled placeholders rather than photo results: clients show photo results
    /// as bare thumbnails — no title, no description, in a grid or a list alike — so "photo" and "photo +
    /// text" can't be told apart. Choosing one sends the placeholder, and the chosen-result path swaps the
    /// stored photo into it straight away.
    /// </summary>
    private static List<InlineQueryResult> PhotoResults(PreparedPost post, Uri url)
    {
        var content = new InputTextMessageContent($"{LoadingText}\n{url.AbsoluteUri}");
        List<InlineQueryResult> results =
        [
            new InlineQueryResultArticle(PendingBareId, "🖼 Photo", content) { Description = WithoutTextDescription, ReplyMarkup = OriginalLink(url) },
        ];
        if (post.Body is { } body)
        {
            results.Add(new InlineQueryResultArticle(PendingTextId, "🖼 Photo + text", content) { Description = Preview(body), ReplyMarkup = OriginalLink(url) });
            results.Add(TextOnly(body));
        }

        return results;
    }

    /// <summary>
    /// The bare file, and — when the post has text — the file with the text as its caption, plus the text on
    /// its own. The text-only result is also what keeps the choices readable: clients lay out an all-photo
    /// answer as a grid of thumbnails with no titles, so "photo" and "photo + text" would look identical;
    /// one non-photo result switches them to a list that shows each title and description.
    /// </summary>
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

        List<InlineQueryResult> results = [Media("bare", kind, fileId, label, caption: null, WithoutTextDescription)];
        if (post.Body is { } body)
        {
            results.Add(Media("text", kind, fileId, labelWithText, Fit(body, CaptionLimit), Preview(body)));
            results.Add(TextOnly(body));
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

    private static InlineQueryResultArticle TextOnly(string body) =>
        new("text-only", TextOnlyTitle, new InputTextMessageContent(Fit(body, TextLimit))) { Description = Preview(body) };

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
