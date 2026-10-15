using System.Globalization;
using LeBot.Application.Digest;
using LeBot.Application.Ports;
using LeBot.Application.Telemetry;

namespace LeBot.Application.Tests.Digest;

public class DigestTests
{
    // UTC+3 all year — Kyiv's summer offset — so the tests don't hang on the host's time zone data.
    private static readonly StatsTimeZone Plus3 = new(TimeZoneInfo.CreateCustomTimeZone("plus3", TimeSpan.FromHours(3), "plus3", "plus3"));

    private readonly IChatSettingsStore _settings = Substitute.For<IChatSettingsStore>();
    private readonly IRepostEventStore _events = Substitute.For<IRepostEventStore>();
    private readonly IStatsOptOutStore _optOuts = Substitute.For<IStatsOptOutStore>();

    [Theory]
    [InlineData("2026-10-11T12:00:00+03:00", "2026-10-12T10:00:00+03:00")] // Sunday → the next morning
    [InlineData("2026-10-12T09:59:00+03:00", "2026-10-12T10:00:00+03:00")] // Monday, just before
    [InlineData("2026-10-12T10:00:00+03:00", "2026-10-19T10:00:00+03:00")] // Monday, exactly on time → next week
    [InlineData("2026-10-14T03:00:00+03:00", "2026-10-19T10:00:00+03:00")] // midweek
    public void NextRun_IsTheNextMondayAtTenLocal(string now, string expected)
    {
        DigestSchedule.NextRun(At(now), Plus3).Should().Be(At(expected));
    }

    [Fact]
    public void LastWeek_OnMondayMorning_IsTheMondayToSundayJustEnded()
    {
        var (monday, sunday, from, to) = DigestSchedule.LastWeek(At("2026-10-12T10:00:00+03:00"), Plus3);

        (monday, sunday).Should().Be((new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 11)));
        from.Should().Be(At("2026-10-05T00:00:00+03:00"));
        to.Should().Be(At("2026-10-12T00:00:00+03:00"));
    }

    [Fact]
    public void Build_CountsPlatformsAndTopDroppers_SkippingOptOutsAndUnknownSenders()
    {
        RepostRow[] week =
        [
            new(1, "tiktok.com"), new(1, "vt.tiktok.com"), new(1, "x.com"),
            new(2, "tiktok.com"), new(2, "tiktok.com"),
            new(3, "x.com"),
            new(4, "x.com"), new(4, "x.com"), new(4, "x.com"), new(4, "x.com"),
            new(null, "tiktok.com"),
        ];

        var digest = WeeklyDigestPlanner.Build(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 11), week, previousWeek: 15, optedOut: new HashSet<long> { 4 });

        digest.Reposts.Should().Be(11);
        digest.Change.Should().Be(-4);
        (digest.TopPlatform, digest.TopPlatformReposts).Should().Be(("X", 6)); // opt-outs still count toward the totals
        digest.TopDroppers.Should().Equal((1L, 3), (2L, 2), (3L, 1));
    }

    [Fact]
    public async Task PlanAsync_SkipsChatsWithAQuietWeek()
    {
        _settings.GetDigestChatIdsAsync(Arg.Any<CancellationToken>()).Returns([-1L, -2L]);
        _events.GetRepostRowsAsync(ChatHasher.Of(-1), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([new RepostRow(1, "x.com")]);
        _events.GetRepostRowsAsync(ChatHasher.Of(-2), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _optOuts.GetOptedOutAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(new HashSet<long>());

        var due = await new WeeklyDigestPlanner(_settings, _events, _optOuts, Plus3)
            .PlanAsync(At("2026-10-12T10:00:00+03:00"), CancellationToken.None);

        due.Select(d => d.ChatId).Should().Equal(-1L);
    }

    private static DateTimeOffset At(string instant) => DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture);
}
