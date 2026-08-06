namespace LeBot.Domain.Media;

/// <summary>
/// One later part of a post that the author wrote as a chain — the "2/3", "3/3" the platform shows
/// under the first message. Carries its own text and its own attachments; either may be empty, but
/// a segment with neither is not worth sending.
/// </summary>
/// <param name="Text">What the author wrote in this part; null or blank when the part is media-only.</param>
/// <param name="Items">This part's own attachments, in order. Empty for a text-only part.</param>
public sealed record PostSegment(string? Text, IReadOnlyList<MediaItem> Items)
{
    /// <summary>True when this part has something to say or show.</summary>
    public bool HasContent => Items.Count > 0 || !string.IsNullOrWhiteSpace(Text);
}
