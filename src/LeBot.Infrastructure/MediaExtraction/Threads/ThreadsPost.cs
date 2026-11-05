using LeBot.Domain.Media;

namespace LeBot.Infrastructure.MediaExtraction.Threads;

/// <summary>
/// One Threads post as the page's own payload describes it: who wrote it, what they wrote, every
/// attachment in order, and the parts the author chained after it. Media are still remote URLs
/// here — downloading is the extractor's job.
/// </summary>
internal sealed record ThreadsPost(
    string? Author,
    string? Caption,
    IReadOnlyList<ThreadsMediaSource> Media,
    IReadOnlyList<ThreadsPostPart> Continuation)
{
    /// <summary>
    /// What the page says the post carries that the parser found no media in — named by payload
    /// key (<c>giphy_media_info</c>, <c>linked_inline_media</c>) or by the media type the node
    /// claims (<c>media_type 2</c>). Empty whenever <see cref="Media"/> has anything in it. A
    /// non-empty list is the parser saying "not text-only, and not readable either": the shape to
    /// teach it next.
    /// </summary>
    public IReadOnlyList<string> UnreadAttachments { get; init; } = [];

    /// <summary>
    /// True when the post is words and nothing else — the one post a card rendering it is honest
    /// about.
    /// </summary>
    public bool IsTextOnly => Media.Count == 0 && UnreadAttachments.Count == 0;
}

/// <summary>One later part of the author's own chain — the "2/3" under the post.</summary>
internal sealed record ThreadsPostPart(string? Text, IReadOnlyList<ThreadsMediaSource> Media);

/// <summary>A single downloadable attachment of a post.</summary>
internal readonly record struct ThreadsMediaSource(string Url, MediaKind Kind);
