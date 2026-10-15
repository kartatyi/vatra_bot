using LeBot.Domain.Media;

namespace LeBot.Domain.Settings;

/// <summary>
/// One chat's choices for how reposts carry the post's text. Video-led and text-led posts are set
/// separately because their text plays opposite roles: under a TikTok it is mostly hashtags and promo,
/// while a Threads or X post <i>is</i> its text.
/// </summary>
/// <param name="VideoCaption">Applies to video-led posts (see <see cref="ModeFor"/>). Defaults to <see cref="CaptionMode.Off"/>.</param>
/// <param name="PostCaption">
/// Applies to text-led posts. Never <see cref="CaptionMode.Off"/>: dropping the text of a text-led post
/// leaves nothing worth reposting.
/// </param>
/// <param name="OfferFullText">
/// Whether a repost whose text was dropped or cut offers a button that shows the rest. On by default:
/// the chat stays tidy, and the one post where the text matters is a tap away.
/// </param>
public sealed record ChatSettings(CaptionMode VideoCaption, CaptionMode PostCaption, bool OfferFullText = true)
{
    /// <summary>What a chat gets before anyone has opened <c>/settings</c>.</summary>
    public static ChatSettings Default { get; } = new(CaptionMode.Off, CaptionMode.Full);

    /// <summary>Hosts whose posts are text-led even when they carry a video.</summary>
    private static readonly string[] TextLedHosts =
        ["threads.net", "threads.com", "x.com", "twitter.com", "reddit.com", "redd.it"];

    public CaptionMode PostCaption { get; } = PostCaption != CaptionMode.Off
        ? PostCaption
        : throw new ArgumentOutOfRangeException(nameof(PostCaption), PostCaption, "A text-led post can't drop its text.");

    /// <summary>
    /// The mode for <paramref name="payload"/>: <see cref="VideoCaption"/> when the post is video-led —
    /// it carries a video and doesn't come from a text-first platform — otherwise <see cref="PostCaption"/>.
    /// A photo post counts as text-led: its caption is usually what the photos are about.
    /// </summary>
    public CaptionMode ModeFor(MediaPayload payload) =>
        IsVideoLed(payload) ? VideoCaption : PostCaption;

    /// <summary>
    /// <paramref name="payload"/> as this chat should see it: untouched under <see cref="CaptionMode.Full"/>,
    /// its text run through <see cref="CaptionShortener"/> under <see cref="CaptionMode.Brief"/>, and stripped
    /// of every caption source — title and author included — under <see cref="CaptionMode.Off"/>. When that
    /// changed the text and <see cref="OfferFullText"/> is on, the original <see cref="MediaPayload.Body"/>
    /// rides along as <see cref="MediaPayload.RevealableText"/>. The media and file ownership never change.
    /// </summary>
    public MediaPayload ShapeCaption(MediaPayload payload)
    {
        var shaped = ModeFor(payload) switch
        {
            CaptionMode.Full => payload,
            CaptionMode.Brief => payload with
            {
                Title = null,
                Description = CaptionShortener.Shorten(
                    string.IsNullOrWhiteSpace(payload.Description) ? payload.Title : payload.Description),
            },
            _ => payload with { Title = null, Description = null, Author = null },
        };

        return OfferFullText && payload.Body is { } full && shaped.Body != full
            ? shaped with { RevealableText = full }
            : shaped;
    }

    private static bool IsVideoLed(MediaPayload payload) =>
        payload.Items.Any(item => item.Kind == MediaKind.Video) && !IsTextLedHost(payload.SourceUrl);

    private static bool IsTextLedHost(Uri url) =>
        TextLedHosts.Any(host =>
            url.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
            url.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));
}
