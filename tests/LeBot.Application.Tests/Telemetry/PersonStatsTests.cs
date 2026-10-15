using LeBot.Application.Telemetry;

namespace LeBot.Application.Tests.Telemetry;

public class PersonStatsTests
{
    private const long ChatId = -100123;
    private static readonly string Chat = ChatHasher.Of(ChatId);

    // UTC+3, like Kyiv in summer, without depending on the host's time zone database.
    private static readonly StatsTimeZone Plus3 = new(TimeZoneInfo.CreateCustomTimeZone("plus3", TimeSpan.FromHours(3), "plus3", "plus3"));

    private readonly IRepostEventStore _events = Substitute.For<IRepostEventStore>();
    private readonly IPersonStatsStore _people = Substitute.For<IPersonStatsStore>();

    public PersonStatsTests()
    {
        _events.GetSenderHostCountsAsync(Chat, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([new SenderHostCount(1, "tiktok.com", 10), new SenderHostCount(2, "x.com", 5), new SenderHostCount(3, "x.com", 1)]);
        _people.GetRepostMomentsAsync(Chat, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns([]);
        _people.GetRepeatCountsAsync(Chat, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns([]);
        _people.GetTrendsetterCountsAsync(Chat, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns([]);
    }

    [Fact]
    public async Task GetRankingAsync_GivesEachBadgeToItsLeader()
    {
        _people.GetTrendsetterCountsAsync(Chat, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([new SenderCount(2, 3), new SenderCount(1, 1)]);
        _people.GetRepeatCountsAsync(Chat, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([new SenderCount(3, 2)]);
        _people.GetRepostMomentsAsync(Chat, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([
                new SenderMoment(2, At(utcHour: 22)), // 01:00 local — night
                new SenderMoment(1, At(utcHour: 10)), // 13:00 local — day
            ]);

        var (ranking, badges) = await Sut().GetRankingAsync(ChatId, DateTimeOffset.MinValue, CancellationToken.None);

        ranking.Select(e => e.SenderId).Should().Equal(1, 2, 3);
        badges.Should().BeEquivalentTo(new Dictionary<long, IReadOnlyList<Badge>>
        {
            [2] = [Badge.Trendsetter, Badge.NightOwl],
            [3] = [Badge.RepeatKing],
        });
    }

    [Fact]
    public async Task GetRankingAsync_TieGoesToWhoeverRanksHigher()
    {
        _people.GetRepeatCountsAsync(Chat, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([new SenderCount(3, 2), new SenderCount(1, 2)]);

        var (_, badges) = await Sut().GetRankingAsync(ChatId, DateTimeOffset.MinValue, CancellationToken.None);

        badges.Should().ContainKey(1).WhoseValue.Should().Equal(Badge.RepeatKing);
    }

    [Fact]
    public async Task GetRankingAsync_NobodyScores_NoBadges()
    {
        var (_, badges) = await Sut().GetRankingAsync(ChatId, DateTimeOffset.MinValue, CancellationToken.None);

        badges.Should().BeEmpty();
    }

    [Fact]
    public async Task GetPersonalAsync_BuildsTheCardInLocalTime()
    {
        _people.GetRepostMomentsAsync(Chat, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([
                new SenderMoment(2, At(utcHour: 20)), // 23:00 local
                new SenderMoment(2, At(utcHour: 20)),
                new SenderMoment(2, At(utcHour: 9)),  // 12:00 local
                new SenderMoment(1, At(utcHour: 20)),
            ]);
        _people.GetTrendsetterCountsAsync(Chat, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns([new SenderCount(2, 6)]);
        _people.GetRepeatCountsAsync(Chat, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns([new SenderCount(2, 4)]);

        var card = await Sut().GetPersonalAsync(ChatId, 2, DateTimeOffset.MinValue, CancellationToken.None);

        card.Should().Be(new PersonalStats(5, 2, 3, "X", 5, 23, 6, 4));
    }

    [Fact]
    public async Task GetPersonalAsync_NothingOfTheirs_IsNull()
    {
        (await Sut().GetPersonalAsync(ChatId, 99, DateTimeOffset.MinValue, CancellationToken.None)).Should().BeNull();
    }

    private PersonStats Sut() => new(_events, _people, Plus3);

    private static DateTimeOffset At(int utcHour) => new(2026, 10, 14, utcHour, 0, 0, TimeSpan.Zero);
}
