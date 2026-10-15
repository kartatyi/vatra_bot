namespace LeBot.Application.Telemetry;

/// <summary>
/// Turns a chat's per-person, per-host repost counts into its <c>/leaders</c> ranking: most reposts first,
/// each person with the platform most of their links came from.
/// </summary>
public static class Leaderboard
{
    /// <summary>
    /// The top <paramref name="limit"/> people. Ties break by user id so the order is stable between calls;
    /// a tie for favourite platform breaks alphabetically for the same reason.
    /// </summary>
    public static IReadOnlyList<LeaderboardEntry> Rank(IEnumerable<SenderHostCount> rows, int limit) =>
        rows
            .GroupBy(row => row.SenderId)
            .Select(person =>
            {
                var top = person
                    .GroupBy(row => PlatformName.Of(row.Host))
                    .Select(platform => (Name: platform.Key, Reposts: platform.Sum(row => row.Reposts)))
                    .OrderByDescending(platform => platform.Reposts)
                    .ThenBy(platform => platform.Name, StringComparer.Ordinal)
                    .First();
                return new LeaderboardEntry(person.Key, person.Sum(row => row.Reposts), top.Name, top.Reposts);
            })
            .OrderByDescending(entry => entry.Reposts)
            .ThenBy(entry => entry.SenderId)
            .Take(limit)
            .ToList();
}
