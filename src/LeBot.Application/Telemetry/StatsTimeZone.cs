namespace LeBot.Application.Telemetry;

/// <summary>
/// The time zone "night" and "most active around" are read in. A group's members share a clock, and it
/// isn't UTC: 23:00 in Kyiv is 20:00 or 21:00 in UTC, depending on the season.
/// </summary>
/// <param name="Zone">The chat's local time zone.</param>
public sealed record StatsTimeZone(TimeZoneInfo Zone)
{
    /// <summary>The local hour (0–23) of <paramref name="instant"/>.</summary>
    public int HourOf(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Zone).Hour;
}
