using LeBot.Application.Telemetry;

namespace LeBot.Application.Tests.Telemetry;

public class LeaderboardTests
{
    [Fact]
    public void Rank_OrdersByRepostsAndPicksEachPersonsTopPlatform()
    {
        SenderHostCount[] rows =
        [
            new(1, "tiktok.com", 3),
            new(1, "vt.tiktok.com", 4),
            new(1, "instagram.com", 5),
            new(2, "x.com", 2),
        ];

        var ranking = Leaderboard.Rank(rows, limit: 10);

        ranking.Should().Equal(
            new LeaderboardEntry(1, 12, "TikTok", 7),
            new LeaderboardEntry(2, 2, "X", 2));
    }

    [Fact]
    public void Rank_Ties_BreakBySenderIdAndPlatformName()
    {
        SenderHostCount[] rows =
        [
            new(9, "youtube.com", 2),
            new(9, "instagram.com", 2),
            new(4, "reddit.com", 4),
        ];

        var ranking = Leaderboard.Rank(rows, limit: 10);

        ranking.Should().Equal(
            new LeaderboardEntry(4, 4, "Reddit", 4),
            new LeaderboardEntry(9, 4, "Instagram", 2));
    }

    [Fact]
    public void Rank_HonoursLimit()
    {
        var rows = Enumerable.Range(1, 20).Select(id => new SenderHostCount(id, "tiktok.com", id));

        var ranking = Leaderboard.Rank(rows, limit: 3);

        ranking.Select(entry => entry.SenderId).Should().Equal(20, 19, 18);
    }

    [Fact]
    public void Rank_NoRows_IsEmpty()
    {
        Leaderboard.Rank([], limit: 10).Should().BeEmpty();
    }
}
