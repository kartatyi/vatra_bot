using LeBot.Application.Telemetry;

namespace LeBot.Application.Digest;

/// <summary>
/// When the weekly digest goes out — Monday, 10:00 in the chat's time zone — and which week it covers:
/// the Monday-to-Sunday that just ended, in that same zone.
/// </summary>
public static class DigestSchedule
{
    /// <summary>The local time of day the digest is posted.</summary>
    public static readonly TimeOnly PostAt = new(10, 0);

    /// <summary>The first posting moment strictly after <paramref name="now"/>.</summary>
    public static DateTimeOffset NextRun(DateTimeOffset now, StatsTimeZone timeZone)
    {
        var local = TimeZoneInfo.ConvertTime(now, timeZone.Zone);
        var daysUntilMonday = ((int)DayOfWeek.Monday - (int)local.DayOfWeek + 7) % 7;
        var candidate = ToInstant(DateOnly.FromDateTime(local.Date).AddDays(daysUntilMonday), PostAt, timeZone);
        return candidate > now ? candidate : ToInstant(DateOnly.FromDateTime(local.Date).AddDays(daysUntilMonday + 7), PostAt, timeZone);
    }

    /// <summary>
    /// The last full week before <paramref name="now"/>: its local Monday and Sunday, and the instants
    /// bounding it — from Monday 00:00 up to, not including, the next Monday 00:00.
    /// </summary>
    public static (DateOnly Monday, DateOnly Sunday, DateTimeOffset From, DateTimeOffset To) LastWeek(DateTimeOffset now, StatsTimeZone timeZone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, timeZone.Zone).Date);
        var thisMonday = today.AddDays(-(((int)today.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7));
        var lastMonday = thisMonday.AddDays(-7);
        return (lastMonday, thisMonday.AddDays(-1), ToInstant(lastMonday, TimeOnly.MinValue, timeZone), ToInstant(thisMonday, TimeOnly.MinValue, timeZone));
    }

    private static DateTimeOffset ToInstant(DateOnly date, TimeOnly time, StatsTimeZone timeZone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, timeZone.Zone.GetUtcOffset(local));
    }
}
