using LeBot.Application.UseCases.AnswerInlineQuery;
using LeBot.Domain.Media;
using Telegram.Bot.Types.InlineQueryResults;
using Telegram.Bot.Types.ReplyMarkups;

namespace LeBot.Infrastructure.Telegram;

/// <summary>
/// Turns an <see cref="InlineAnswer"/> into the results Telegram shows above the keyboard, and how long
/// Telegram may cache them, in the language of whoever typed the query. Pure, so the copy and the shapes are
/// testable without Telegram.
/// </summary>
internal static class InlineResults
{
    internal const int CaptionLimit = 1024;
    internal const int TextLimit = 4096;

    private const string PendingBareId = "pending:bare";
    private const string PendingTextId = "pending:text";
    private const int PreviewLength = 80;

    public static string PendingTitle(BotLanguage lang) => lang.Pick("⏳ Медіа — з'явиться за мить", "⏳ Media — appears in a moment");

    public static string PendingWithTextTitle(BotLanguage lang) => lang.Pick("⏳ Медіа + текст — з'явиться за мить", "⏳ Media + text — appears in a moment");

    public static string LoadingText(BotLanguage lang) => lang.Pick("⏳ Завантажую медіа…", "⏳ Loading the media…");

    public static string OriginalButton(BotLanguage lang) => lang.Pick("🔗 Оригінал", "🔗 Original");

    public static string FailedTitle(BotLanguage lang) => lang.Pick("🤷 Не вдалося дістати медіа з цього лінка", "🤷 Couldn't get media from this link");

    public static string WithoutTextDescription(BotLanguage lang) => lang.Pick("Без тексту поста", "Without the post's text");

    /// <summary>
    /// The results for <paramref name="answer"/>, and how long Telegram may cache them. Only a text-only post
    /// is cached: every media choice is a placeholder the chosen-result path fills in, and "not ready" must
    /// let the next keystroke reach the finished preparation.
    /// </summary>
    public static (IReadOnlyList<InlineQueryResult> Results, int CacheSeconds) For(InlineAnswer answer, BotLanguage lang = BotLanguage.En) => answer switch
    {
        InlineAnswer.Ready { Post.HasMedia: true } ready => (MediaResults(ready.Post, ready.Url, lang), 0),
        InlineAnswer.Ready ready => (TextResults(ready.Post, lang), 300),
        InlineAnswer.NotReady notReady => (PendingResults(notReady.Url, lang), 0),
        InlineAnswer.Failed failed => ([LinkArticle("failed", FailedTitle(lang), failed.Url)], 0),
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
    public static InlineKeyboardMarkup OriginalLink(Uri url, BotLanguage lang) =>
        new(InlineKeyboardButton.WithUrl(OriginalButton(lang), url.AbsoluteUri));

    /// <summary>
    /// Two placeholders mirroring the ready choices. The keyboard isn't decoration: Telegram only reports
    /// which inline message was sent — the handle the bot needs to edit it later — when it carries one.
    /// </summary>
    private static List<InlineQueryResult> PendingResults(Uri url, BotLanguage lang)
    {
        var content = Loading(url, lang);
        return
        [
            new InlineQueryResultArticle(PendingBareId, PendingTitle(lang), content) { Description = url.AbsoluteUri, ReplyMarkup = OriginalLink(url, lang) },
            new InlineQueryResultArticle(PendingTextId, PendingWithTextTitle(lang), content) { Description = url.AbsoluteUri, ReplyMarkup = OriginalLink(url, lang) },
        ];
    }

    /// <summary>
    /// A media post's choices, as titled placeholders rather than media results: clients show photo and video
    /// results as bare thumbnails — no title, no description — so "with text" and "without" can't be told
    /// apart. Choosing one sends the placeholder, and the chosen-result path swaps the stored media into it
    /// straight away: it's already in the storage chat. Two choices at most — the media, or the media with
    /// the post's text.
    /// </summary>
    private static List<InlineQueryResult> MediaResults(PreparedPost post, Uri url, BotLanguage lang)
    {
        var (label, labelWithText) = post.Kind switch
        {
            MediaKind.Photo => (lang.Pick("🖼 Фото", "🖼 Photo"), lang.Pick("🖼 Фото + текст", "🖼 Photo + text")),
            MediaKind.Animation => ("🎞 GIF", lang.Pick("🎞 GIF + текст", "🎞 GIF + text")),
            MediaKind.Audio => (lang.Pick("🎵 Аудіо", "🎵 Audio"), lang.Pick("🎵 Аудіо + текст", "🎵 Audio + text")),
            _ => (lang.Pick("📹 Відео", "📹 Video"), lang.Pick("📹 Відео + текст", "📹 Video + text")),
        };

        var content = Loading(url, lang);
        List<InlineQueryResult> results =
        [
            new InlineQueryResultArticle(PendingBareId, label, content) { Description = WithoutTextDescription(lang), ReplyMarkup = OriginalLink(url, lang) },
        ];
        if (post.Body is { } body)
        {
            results.Add(new InlineQueryResultArticle(PendingTextId, labelWithText, content) { Description = Preview(body), ReplyMarkup = OriginalLink(url, lang) });
        }

        return results;
    }

    private static List<InlineQueryResult> TextResults(PreparedPost post, BotLanguage lang) =>
        post.Body is { } body
            ? [new InlineQueryResultArticle("text", lang.Pick("📝 Текст", "📝 Text"), new InputTextMessageContent(Fit(body, TextLimit))) { Description = Preview(body) }]
            : [];

    private static InputTextMessageContent Loading(Uri url, BotLanguage lang) => new($"{LoadingText(lang)}\n{url.AbsoluteUri}");

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
