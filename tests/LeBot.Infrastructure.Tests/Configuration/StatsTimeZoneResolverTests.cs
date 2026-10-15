using LeBot.Infrastructure.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LeBot.Infrastructure.Tests.Configuration;

public class StatsTimeZoneResolverTests
{
    [Fact]
    public void Resolve_Kyiv_FindsAZoneThreeHoursAheadInSummer()
    {
        var zone = StatsTimeZoneResolver.Resolve("Europe/Kyiv", NullLogger.Instance);

        zone.HourOf(new DateTimeOffset(2026, 7, 1, 20, 0, 0, TimeSpan.Zero)).Should().Be(23);
    }

    [Fact]
    public void Resolve_UnknownZone_FallsBackToUtc()
    {
        var zone = StatsTimeZoneResolver.Resolve("Mars/Olympus_Mons", NullLogger.Instance);

        zone.Zone.Should().Be(TimeZoneInfo.Utc);
    }
}
