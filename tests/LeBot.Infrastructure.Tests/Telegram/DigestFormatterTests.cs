using LeBot.Application.Digest;
using LeBot.Infrastructure.Telegram;

namespace LeBot.Infrastructure.Tests.Telegram;

public class DigestFormatterTests
{
    private static WeeklyDigest Digest(int change, DateOnly? start = null, DateOnly? end = null) =>
        new(start ?? new DateOnly(2026, 10, 6), end ?? new DateOnly(2026, 10, 12), 54, change, "TikTok", 31, []);

    [Fact]
    public void Format_FullWeek_MatchesTheApprovedCopy()
    {
        var text = DigestFormatter.Format(Digest(12), [("Petro", 18), ("Olena", 11), ("Ivan", 7)]);

        text.Should().Be(
            "📬 Week in links · Oct 6–12\n\n" +
            "🔗 54 links reposted (↑12 vs last week)\n" +
            "📱 Top platform: TikTok — 31\n" +
            "🏆 Top droppers: 🥇 Petro 18 · 🥈 Olena 11 · 🥉 Ivan 7\n\n" +
            "/leaders for the full ranking · /settings to turn the digest off");
    }

    [Theory]
    [InlineData(-5, "(↓5 vs last week)")]
    [InlineData(0, "(same as last week)")]
    public void Format_ChangeReadsNaturally(int change, string expected)
    {
        DigestFormatter.Format(Digest(change), []).Should().Contain(expected);
    }

    [Fact]
    public void Format_WeekAcrossTwoMonths_NamesBoth()
    {
        DigestFormatter.Format(Digest(1, new DateOnly(2026, 9, 28), new DateOnly(2026, 10, 4)), [])
            .Should().StartWith("📬 Week in links · Sep 28 – Oct 4");
    }

    [Fact]
    public void Format_NoNamedDroppers_LeavesTheLineOut()
    {
        DigestFormatter.Format(Digest(1), []).Should().NotContain("Top droppers");
    }
}
