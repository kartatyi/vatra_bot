using System.Globalization;
using System.Text;
using LeBot.Application.Metrics;
using LeBot.Application.Telemetry;

namespace LeBot.Infrastructure.Telegram;

/// <summary>
/// Renders the dashboard commands' Telegram replies as plain text — pure string building, kept out of the
/// dispatcher so it can be unit-tested without a bot client. Plain text (no Markdown/HTML parse mode) on
/// purpose: URLs carry <c>_</c>, <c>*</c>, <c>~</c> that would otherwise need escaping. Every message is
/// capped at Telegram's <see cref="TelegramMaxMessageLength"/> limit.
/// </summary>
internal static class DashboardReportFormatter
{
    private const int TelegramMaxMessageLength = 4096;

    /// <summary>How long an error reason may run before it's clipped, so one noisy yt-dlp dump can't eat a whole reply.</summary>
    private const int MaxReasonLength = 160;

    /// <summary>
    /// <c>/stats</c>: the in-memory "since boot" counters merged with the durable all-time rollup.
    /// </summary>
    public static string Stats(RepostMetrics metrics, TimeSpan uptime, RepostStatsSnapshot allTime)
    {
        var sb = new StringBuilder();
        sb.Append("📊 Stats\n\n");
        sb.Append("Uptime: ").Append(FormatUptime(uptime)).Append('\n');

        sb.Append("Since boot — media ").Append(Num(metrics.MediaReposts))
            .Append(", text ").Append(Num(metrics.TextReposts))
            .Append(", failures ").Append(Num(metrics.Failures))
            .Append(", skipped ").Append(Num(metrics.SilentSkips)).Append('\n');

        foreach (var pair in metrics.ByExtractor.OrderByDescending(kv => kv.Value))
        {
            sb.Append("  • ").Append(pair.Key).Append(": ").Append(Num(pair.Value)).Append('\n');
        }

        sb.Append('\n');
        if (allTime.TotalProcessed == 0)
        {
            sb.Append("All-time: no history recorded yet");
            return Cap(sb.ToString());
        }

        var since = allTime.FirstEventAt is { } first
            ? first.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : "?";
        sb.Append("All-time (since ").Append(since).Append("): ")
            .Append(Num(allTime.TotalProcessed)).Append(" processed\n");
        sb.Append("✓ ").Append(Percent(allTime.SuccessRate)).Append(" success — media ")
            .Append(Num(allTime.MediaReposts)).Append(", text ").Append(Num(allTime.TextFallbacks)).Append('\n');
        sb.Append("✗ failures ").Append(Num(allTime.Failures))
            .Append(", nothing ").Append(Num(allTime.NothingExtracted))
            .Append(", no-extractor ").Append(Num(allTime.NoExtractor)).Append('\n');
        sb.Append("Chats seen: ").Append(Num(allTime.DistinctChats));

        return Cap(sb.ToString());
    }

    /// <summary>
    /// <c>/failures</c>: the most recent broken links, each with the error the extractor reported.
    /// </summary>
    public static string Failures(IReadOnlyList<RecentFailure> failures, DateTimeOffset now)
    {
        if (failures.Count == 0)
        {
            return "No failures recorded 🎉";
        }

        var sb = new StringBuilder();
        sb.Append("🚫 Last ").Append(Num(failures.Count)).Append(" failure(s)\n");
        foreach (var failure in failures)
        {
            sb.Append('\n').Append(Age(failure.OccurredAt, now)).Append(" · ").Append(failure.Host).Append('\n');
            sb.Append(failure.Url).Append('\n');
            var variant = failure.ErrorVariant ?? "Error";
            sb.Append(variant);
            if (!string.IsNullOrWhiteSpace(failure.ErrorReason))
            {
                sb.Append(": ").Append(Clip(failure.ErrorReason, MaxReasonLength));
            }

            sb.Append('\n');
        }

        return Cap(sb.ToString());
    }

    /// <summary>
    /// <c>/top</c>: the busiest platforms and the ones breaking most often.
    /// </summary>
    public static string Top(IReadOnlyList<HostStat> byVolume, IReadOnlyList<HostStat> byFailureRate, int failureRateMinVolume)
    {
        if (byVolume.Count == 0)
        {
            return "No platform data yet.";
        }

        var sb = new StringBuilder();
        sb.Append("🏆 Top platforms\n\nBy volume:\n");
        var rank = 1;
        foreach (var host in byVolume)
        {
            sb.Append("  ").Append(rank++).Append(". ").Append(host.Host)
                .Append(" — ").Append(Num(host.Total)).Append('\n');
        }

        sb.Append("\nBy failure rate (≥").Append(failureRateMinVolume).Append(" posts):\n");
        if (byFailureRate.Count == 0)
        {
            sb.Append("  (none yet)");
        }
        else
        {
            rank = 1;
            foreach (var host in byFailureRate)
            {
                sb.Append("  ").Append(rank++).Append(". ").Append(host.Host)
                    .Append(" — ").Append(Percent(host.FailureRate))
                    .Append(" (").Append(Num(host.Failures)).Append('/').Append(Num(host.Total)).Append(")\n");
            }
        }

        return Cap(sb.ToString());
    }

    private static string Age(DateTimeOffset when, DateTimeOffset now)
    {
        var elapsed = now - when;
        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            return $"{(int)elapsed.TotalMinutes}m ago";
        }

        if (elapsed < TimeSpan.FromDays(1))
        {
            return $"{(int)elapsed.TotalHours}h ago";
        }

        return $"{(int)elapsed.TotalDays}d ago";
    }

    private static string FormatUptime(TimeSpan uptime) =>
        uptime.TotalDays >= 1
            ? $"{(int)uptime.TotalDays}d {uptime:hh\\:mm}"
            : uptime.ToString("hh\\:mm\\:ss", CultureInfo.InvariantCulture);

    private static string Num(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Percent(double fraction) =>
        (fraction * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private static string Clip(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private static string Cap(string message) =>
        message.Length <= TelegramMaxMessageLength
            ? message
            : message[..(TelegramMaxMessageLength - 1)] + "…";
}
