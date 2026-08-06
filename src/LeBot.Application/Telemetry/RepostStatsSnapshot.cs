namespace LeBot.Application.Telemetry;

/// <summary>
/// A durable rollup of the repost journal over a time window — the numbers behind the dashboard's KPI
/// cards and the <c>/stats</c> command's "since boot vs all time" split. Unlike the in-memory
/// <see cref="Metrics.RepostMetrics"/> (which resets on restart), this is computed from the persisted
/// rows, so it survives restarts and self-updates.
/// </summary>
/// <param name="TotalProcessed">Every journalled outcome in the window (the denominator for <see cref="SuccessRate"/>).</param>
/// <param name="MediaReposts">URLs that produced media sent to the chat.</param>
/// <param name="TextFallbacks">URLs that produced a text reply when no media was available.</param>
/// <param name="Failures">URLs an extractor claimed but failed hard on — the "why it breaks" signal.</param>
/// <param name="NothingExtracted">URLs an extractor claimed but returned an empty payload for.</param>
/// <param name="NoExtractor">URLs no extractor ultimately claimed (all returned UnsupportedPlatform).</param>
/// <param name="DistinctChats">Number of distinct (hashed) chats seen in the window.</param>
/// <param name="FirstEventAt">When the earliest event in the window occurred; null when the window is empty.</param>
/// <param name="LastEventAt">When the most recent event in the window occurred; null when the window is empty.</param>
public sealed record RepostStatsSnapshot(
    int TotalProcessed,
    int MediaReposts,
    int TextFallbacks,
    int Failures,
    int NothingExtracted,
    int NoExtractor,
    int DistinctChats,
    DateTimeOffset? FirstEventAt,
    DateTimeOffset? LastEventAt)
{
    /// <summary>The zero snapshot, for an empty window.</summary>
    public static RepostStatsSnapshot Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, null, null);

    /// <summary>
    /// The bot did something useful with the URL — sent media or, failing that, the post's text.
    /// (<see cref="NothingExtracted"/> / <see cref="NoExtractor"/> are neither successes nor hard failures.)
    /// </summary>
    public int Successes => MediaReposts + TextFallbacks;

    /// <summary>Fraction of processed URLs that ended in a <see cref="Successes">success</see>; 0 for an empty window.</summary>
    public double SuccessRate => TotalProcessed == 0 ? 0d : (double)Successes / TotalProcessed;
}
