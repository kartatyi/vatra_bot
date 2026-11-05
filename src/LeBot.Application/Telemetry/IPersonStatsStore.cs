namespace LeBot.Application.Telemetry;

/// <summary>
/// The per-person reads behind the <c>/leaders</c> badges and <c>/me</c>. Every read is scoped to one chat
/// and a window starting at <c>since</c>, and leaves out everyone who opted out of that chat's ranking.
/// </summary>
public interface IPersonStatsStore
{
    /// <summary>When each person's links were reposted (media or text) in the window.</summary>
    Task<IReadOnlyList<SenderMoment>> GetRepostMomentsAsync(string chatHash, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>How many links each person posted that the chat had already seen.</summary>
    Task<IReadOnlyList<SenderCount>> GetRepeatCountsAsync(string chatHash, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>
    /// How many links each person brought first, in the window, that somebody else posted again later.
    /// Re-posting your own link doesn't count — that's not a trend, that's an echo.
    /// </summary>
    Task<IReadOnlyList<SenderCount>> GetTrendsetterCountsAsync(string chatHash, DateTimeOffset since, CancellationToken cancellationToken);
}
