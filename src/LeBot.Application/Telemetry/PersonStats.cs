namespace LeBot.Application.Telemetry;

/// <summary>
/// Puts together a chat's <c>/leaders</c> ranking with its badges, and one person's <c>/me</c> card. The
/// stores answer with raw per-person rows; the ranking, the badges and the local-time reading happen here.
/// </summary>
public sealed class PersonStats(IRepostEventStore events, IPersonStatsStore people, StatsTimeZone timeZone)
{
    /// <summary>Local hours counted as night for the 🦉 badge: from midnight up to, not including, 5 am.</summary>
    private const int NightEndsAtHour = 5;

    /// <summary>
    /// The chat's ranking over the window, best first, ranked in full — the caller decides how many it
    /// shows — with each badge given to the one person who leads its category. A badge goes unawarded
    /// when nobody scores in it; a tie goes to whoever ranks higher.
    /// </summary>
    public async Task<(IReadOnlyList<LeaderboardEntry> Ranking, IReadOnlyDictionary<long, IReadOnlyList<Badge>> Badges)> GetRankingAsync(
        long chatId,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        var chatHash = ChatHasher.Of(chatId);
        var ranking = Leaderboard.Rank(await events.GetSenderHostCountsAsync(chatHash, since, cancellationToken), int.MaxValue);
        var order = ranking.Select((entry, index) => (entry.SenderId, index)).ToDictionary(p => p.SenderId, p => p.index);

        var night = (await people.GetRepostMomentsAsync(chatHash, since, cancellationToken))
            .Where(moment => timeZone.HourOf(moment.At) < NightEndsAtHour)
            .GroupBy(moment => moment.SenderId)
            .Select(group => new SenderCount(group.Key, group.Count()));
        var trendsets = await people.GetTrendsetterCountsAsync(chatHash, since, cancellationToken);
        var repeats = await people.GetRepeatCountsAsync(chatHash, since, cancellationToken);

        var badges = new Dictionary<long, List<Badge>>();
        Award(Badge.Trendsetter, trendsets);
        Award(Badge.NightOwl, night);
        Award(Badge.RepeatKing, repeats);

        return (ranking, badges.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<Badge>)pair.Value));

        void Award(Badge badge, IEnumerable<SenderCount> tallies)
        {
            var winner = tallies
                .Where(tally => tally.Count > 0 && order.ContainsKey(tally.SenderId))
                .OrderByDescending(tally => tally.Count)
                .ThenBy(tally => order[tally.SenderId])
                .FirstOrDefault();
            if (winner is not null)
            {
                (badges.TryGetValue(winner.SenderId, out var held) ? held : badges[winner.SenderId] = []).Add(badge);
            }
        }
    }

    /// <summary>
    /// <paramref name="userId"/>'s card for the chat over the window, or <see langword="null"/> when the
    /// bot reposted nothing of theirs in it.
    /// </summary>
    public async Task<PersonalStats?> GetPersonalAsync(long chatId, long userId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var chatHash = ChatHasher.Of(chatId);
        var ranking = Leaderboard.Rank(await events.GetSenderHostCountsAsync(chatHash, since, cancellationToken), int.MaxValue);
        var index = ranking.ToList().FindIndex(entry => entry.SenderId == userId);
        if (index < 0)
        {
            return null;
        }

        var mine = ranking[index];
        var peakHour = (await people.GetRepostMomentsAsync(chatHash, since, cancellationToken))
            .Where(moment => moment.SenderId == userId)
            .GroupBy(moment => timeZone.HourOf(moment.At))
            .OrderByDescending(hour => hour.Count())
            .ThenBy(hour => hour.Key)
            .Select(hour => hour.Key)
            .FirstOrDefault();
        var trendsets = CountFor(await people.GetTrendsetterCountsAsync(chatHash, since, cancellationToken));
        var repeats = CountFor(await people.GetRepeatCountsAsync(chatHash, since, cancellationToken));

        return new PersonalStats(
            mine.Reposts, index + 1, ranking.Count, mine.TopPlatform, mine.TopPlatformReposts, peakHour, trendsets, repeats);

        int CountFor(IReadOnlyList<SenderCount> tallies) =>
            tallies.FirstOrDefault(tally => tally.SenderId == userId)?.Count ?? 0;
    }
}
