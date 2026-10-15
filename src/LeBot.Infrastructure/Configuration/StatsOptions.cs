using LeBot.Application.Telemetry;
using Microsoft.Extensions.Logging;

namespace LeBot.Infrastructure.Configuration;

/// <summary>How the per-person stats read local time (<c>Stats</c> section).</summary>
public sealed class StatsOptions
{
    public const string SectionName = "Stats";

    /// <summary>IANA or Windows time zone id the chat lives in — "night" and "most active around" are read in it.</summary>
    public string TimeZone { get; init; } = "Europe/Kyiv";
}

/// <summary>Turns <see cref="StatsOptions.TimeZone"/> into a <see cref="StatsTimeZone"/>.</summary>
internal static class StatsTimeZoneResolver
{
    /// <summary>
    /// The configured zone, or UTC when the host doesn't know it. Kyiv gets one fallback: hosts whose time
    /// zone data predates the 2022 rename know it only as "Europe/Kiev".
    /// </summary>
    public static StatsTimeZone Resolve(string id, ILogger logger)
    {
        string[] candidates = id == "Europe/Kyiv" ? [id, "Europe/Kiev"] : [id];
        foreach (var candidate in candidates)
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(candidate, out var zone))
            {
                return new StatsTimeZone(zone);
            }
        }

        logger.LogWarning("Time zone {TimeZone} is unknown on this host; stats read time in UTC", id);
        return new StatsTimeZone(TimeZoneInfo.Utc);
    }
}
