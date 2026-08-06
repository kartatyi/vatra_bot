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
    IReadOnlyList<ThreadsPostPart> Continuation);

/// <summary>One later part of the author's own chain — the "2/3" under the post.</summary>
internal sealed record ThreadsPostPart(string? Text, IReadOnlyList<ThreadsMediaSource> Media);

/// <summary>A single downloadable attachment of a post.</summary>
internal readonly record struct ThreadsMediaSource(string Url, MediaKind Kind);
