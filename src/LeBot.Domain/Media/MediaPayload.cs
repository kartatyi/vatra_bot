namespace LeBot.Domain.Media;

/// <summary>
/// Everything an <c>IPlatformExtractor</c> produced from a source URL: the original URL,
/// optional title, description, and author, and one or more <see cref="MediaItem"/>s ready to ship to Telegram.
/// </summary>
/// <param name="SourceUrl">The URL the user posted in the chat.</param>
/// <param name="Title">A human-readable title pulled from the platform's metadata; may be null.</param>
/// <param name="Author">The uploader / channel / handle; may be null.</param>
/// <param name="Items">One or more media items. Empty payloads are allowed but signal "nothing to repost".</param>
/// <param name="Description">The post's caption / body text from the platform; may be null. Often more useful than <paramref name="Title"/> on Instagram, TikTok, and Threads.</param>
/// <param name="RetainFiles">
/// Who owns the files behind <paramref name="Items"/>. False (the default) means they are throw-away
/// downloads and the sender deletes them once the upload finishes. True means they belong to a
/// longer-lived store — the media cache — and must survive the send so the next repost of the same
/// link can reuse them.
/// </param>
public sealed record MediaPayload(
    Uri SourceUrl,
    string? Title,
    string? Author,
    IReadOnlyList<MediaItem> Items,
    string? Description = null,
    bool RetainFiles = false)
{
    /// <summary>True when the payload contains at least one media item.</summary>
    public bool HasMedia => Items.Count > 0;

    /// <summary>
    /// The text a repost of this payload carries: the description — the post as its author wrote it — or,
    /// failing that, the title, then an "— author" line. Null when there is none of the three. Untrimmed
    /// for length; fitting it to a Telegram limit is the sender's job.
    /// </summary>
    public string? Body
    {
        get
        {
            // The title comes second because yt-dlp often synthesises it ("Video by <uploader>") for short-form.
            var primary = !string.IsNullOrWhiteSpace(Description) ? Description.Trim()
                : !string.IsNullOrWhiteSpace(Title) ? Title.Trim()
                : null;
            var author = !string.IsNullOrWhiteSpace(Author) ? Author.Trim() : null;

            return (primary, author) switch
            {
                (string p, string a) => $"{p}\n\n— {a}",
                (string p, null) => p,
                (null, string a) => $"— {a}",
                _ => null,
            };
        }
    }
}
