using LeBot.Application.Ports;
using LeBot.Application.Telemetry;

namespace LeBot.Application.Digest;

/// <summary>
/// Works out, for every chat that turned the digest on, what its weekly post should say. Chats with no
/// reposts in the week get nothing: an empty digest is noise.
/// </summary>
public sealed class WeeklyDigestPlanner(
    IChatSettingsStore settings,
    IRepostEventStore events,
    IStatsOptOutStore optOuts,
    StatsTimeZone timeZone)
{
    private const int DroppersShown = 3;

    /// <summary>The digests due at <paramref name="now"/>, covering the week that just ended.</summary>
    public async Task<IReadOnlyList<(long ChatId, WeeklyDigest Digest)>> PlanAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var (monday, sunday, from, to) = DigestSchedule.LastWeek(now, timeZone);
        var weekBefore = from - TimeSpan.FromDays(7);
        var due = new List<(long, WeeklyDigest)>();

        foreach (var chatId in await settings.GetDigestChatIdsAsync(cancellationToken))
        {
            var chatHash = ChatHasher.Of(chatId);
            var week = await events.GetRepostRowsAsync(chatHash, from, to, cancellationToken);
            if (week.Count == 0)
            {
                continue;
            }

            var previous = (await events.GetRepostRowsAsync(chatHash, weekBefore, from, cancellationToken)).Count;
            var optedOut = await optOuts.GetOptedOutAsync(chatId, cancellationToken);
            due.Add((chatId, Build(monday, sunday, week, previous, optedOut)));
        }

        return due;
    }

    /// <summary>The digest for one chat's week; <paramref name="week"/> must not be empty.</summary>
    public static WeeklyDigest Build(DateOnly monday, DateOnly sunday, IReadOnlyList<RepostRow> week, int previousWeek, IReadOnlySet<long> optedOut)
    {
        var platform = week
            .GroupBy(row => PlatformName.Of(row.Host))
            .Select(group => (Name: group.Key, Count: group.Count()))
            .OrderByDescending(p => p.Count)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .First();

        var droppers = week
            .Where(row => row.SenderId is { } id && !optedOut.Contains(id))
            .GroupBy(row => row.SenderId ?? 0) // "?? 0" never fires: the filter above keeps known senders only
            .Select(group => (SenderId: group.Key, Reposts: group.Count()))
            .OrderByDescending(d => d.Reposts)
            .ThenBy(d => d.SenderId)
            .Take(DroppersShown)
            .ToList();

        return new WeeklyDigest(monday, sunday, week.Count, week.Count - previousWeek, platform.Name, platform.Count, droppers);
    }
}
