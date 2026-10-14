using LeBot.Application.Telemetry;
using LeBot.Infrastructure.Telegram;

namespace LeBot.Infrastructure.Tests.Telegram;

public class LeaderboardFormatterTests
{
    [Fact]
    public void Format_LastThirtyDays_MedalsTopThreeThenNumbers()
    {
        (string, LeaderboardEntry)[] ranking =
        [
            ("Petro", new(1, 40, "TikTok", 31)),
            ("Olena", new(2, 22, "Instagram", 22)),
            ("Ivan", new(3, 9, "X", 5)),
            ("Mariia", new(4, 2, "Reddit", 2)),
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
    public void Format_AllTime_PointsBackToTheMonthlyView()
    {
        var text = LeaderboardFormatter.Format([("Petro", new(1, 3, "TikTok", 3))], allTime: true);

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
}
