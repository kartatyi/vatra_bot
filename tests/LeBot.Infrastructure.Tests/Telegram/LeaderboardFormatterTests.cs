using LeBot.Application.Telemetry;
using LeBot.Infrastructure.Telegram;

namespace LeBot.Infrastructure.Tests.Telegram;

public class LeaderboardFormatterTests
{
    private static readonly IReadOnlyList<Badge> None = [];

    [Fact]
    public void Format_LastThirtyDays_MedalsTopThreeThenNumbers()
    {
        (string, LeaderboardEntry, IReadOnlyList<Badge>)[] ranking =
        [
            ("Petro", new(1, 40, "TikTok", 31), None),
            ("Olena", new(2, 22, "Instagram", 22), None),
            ("Ivan", new(3, 9, "X", 5), None),
            ("Mariia", new(4, 2, "Reddit", 2), None),
        ];

        var text = LeaderboardFormatter.Format(ranking, allTime: false);

        text.Should().Be(
            "🏆 Top link droppers — last 30 days\n\n" +
            "🥇 Petro — 40 · mostly TikTok (31)\n" +
            "🥈 Olena — 22 · all Instagram\n" +
            "🥉 Ivan — 9 · mostly X (5)\n" +
            "4. Mariia — 2 · all Reddit\n\n" +
            "/leaders all — all time · /leaders optout — leave the ranking");
    }

    [Fact]
    public void Format_Badges_FollowTheirHolderAndOnlyShownOnesAreExplained()
    {
        (string, LeaderboardEntry, IReadOnlyList<Badge>)[] ranking =
        [
            ("Petro", new(1, 40, "TikTok", 31), [Badge.Trendsetter, Badge.NightOwl]),
            ("Olena", new(2, 22, "Instagram", 22), None),
        ];

        var text = LeaderboardFormatter.Format(ranking, allTime: false);

        text.Should().Contain("🥇 Petro — 40 · mostly TikTok (31) ⚡🦉\n")
            .And.Contain("🥈 Olena — 22 · all Instagram\n")
            .And.Contain("\n⚡ first to bring it · 🦉 night owl\n/leaders all")
            .And.NotContain("repeat king");
    }

    [Fact]
    public void Format_AllTime_PointsBackToTheMonthlyView()
    {
        var text = LeaderboardFormatter.Format([("Petro", new(1, 3, "TikTok", 3), None)], allTime: true);

        text.Should().StartWith("🏆 Top link droppers — all time")
            .And.EndWith("/leaders — last 30 days · /leaders optout — leave the ranking");
    }

    [Theory]
    [InlineData(false, "No reposts in this chat in the last 30 days.")]
    [InlineData(true, "No reposts counted in this chat yet.")]
    public void Format_Empty_SaysSo(bool allTime, string expected)
    {
        LeaderboardFormatter.Format([], allTime).Should().Be(expected);
    }

    [Fact]
    public void FormatMe_FullCard()
    {
        var stats = new PersonalStats(40, 1, 12, "TikTok", 31, 23, 6, 4);

        LeaderboardFormatter.FormatMe("Petro", stats, allTime: false).Should().Be(
            "📇 Petro in this chat — last 30 days\n\n" +
            "🔗 40 links · #1 of 12\n" +
            "📱 Mostly TikTok (31)\n" +
            "🕐 Most active around 23:00\n" +
            "⚡ 6 links others posted again later · 🥱 4 repeats\n\n" +
            "/me all — all time");
    }

    [Fact]
    public void FormatMe_NoTrendsOrRepeats_SkipsThatLine_SingularAndPaddedHour()
    {
        var stats = new PersonalStats(1, 3, 3, "X", 1, 7, 0, 0);

        var text = LeaderboardFormatter.FormatMe("Olena", stats, allTime: true);

        text.Should().Be(
            "📇 Olena in this chat — all time\n\n" +
            "🔗 1 link · #3 of 3\n" +
            "📱 All X\n" +
            "🕐 Most active around 07:00\n\n" +
            "/me — last 30 days");
    }

    [Fact]
    public void FormatMe_NothingReposted_SaysSo()
    {
        LeaderboardFormatter.FormatMe("Ivan", null, allTime: false)
            .Should().Be("📇 Ivan in this chat — last 30 days\n\nNo links from you yet.");
    }
}
