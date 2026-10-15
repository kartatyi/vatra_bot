using System.Globalization;
using System.Text;
using LeBot.Application.Digest;

namespace LeBot.Infrastructure.Telegram;

/// <summary>The weekly digest post. Plain text, names written out rather than @-mentioned, so it pings nobody.</summary>
internal static class DigestFormatter
{
    private static readonly string[] Medals = ["🥇", "🥈", "🥉"];

    /// <summary>
    /// The post for <paramref name="digest"/>; <paramref name="names"/> holds the top droppers who could be
    /// named, best first — someone who left the chat is simply missing from it.
    /// </summary>
    public static string Format(WeeklyDigest digest, IReadOnlyList<(string Name, int Reposts)> names)
    {
        var sb = new StringBuilder("📬 Week in links · ").Append(Range(digest.WeekStart, digest.WeekEnd)).Append("\n\n");
        sb.Append("🔗 ").Append(digest.Reposts).Append(digest.Reposts == 1 ? " link reposted" : " links reposted")
            .Append(" (").Append(Change(digest.Change)).Append(")\n");
        sb.Append("📱 Top platform: ").Append(digest.TopPlatform).Append(" — ").Append(digest.TopPlatformReposts).Append('\n');
        if (names.Count > 0)
        {
            sb.Append("🏆 Top droppers: ")
                .Append(string.Join(" · ", names.Select((n, i) => $"{Medals[i]} {n.Name} {n.Reposts}")))
                .Append('\n');
        }

        sb.Append("\n/leaders for the full ranking · /settings to turn the digest off");
        return sb.ToString();
    }

    private static string Change(int change) => change switch
    {
        > 0 => $"↑{change} vs last week",
        < 0 => $"↓{-change} vs last week",
        _ => "same as last week",
    };

    /// <summary>"Oct 6–12" within a month, "Sep 29 – Oct 5" across two.</summary>
    private static string Range(DateOnly start, DateOnly end)
    {
        var culture = CultureInfo.InvariantCulture;
        return start.Month == end.Month
            ? $"{start.ToString("MMM d", culture)}–{end.Day}"
            : $"{start.ToString("MMM d", culture)} – {end.ToString("MMM d", culture)}";
    }
}
