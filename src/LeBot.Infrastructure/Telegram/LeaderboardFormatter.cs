using System.Text;
using LeBot.Application.Telemetry;

namespace LeBot.Infrastructure.Telegram;

/// <summary>
/// The <c>/leaders</c> and <c>/me</c> replies. Plain text with no parse mode, so a member's name can't inject
/// formatting, and names are written out rather than @-mentioned, so the ranking pings nobody.
/// </summary>
internal static class LeaderboardFormatter
{
    public const string OptedOut = "Done — you're out of the ranking in this chat. /leaders optin brings you back.";

    public const string OptedIn = "You're back in the ranking.";

    public const string NoSender = "Couldn't tell who you are — anonymous admins and channels can't opt out.";

    public const string MeOptedOut = "You're out of the ranking in this chat — /leaders optin to see your stats again.";

    private static readonly string[] Medals = ["🥇", "🥈", "🥉"];

    private static readonly (Badge Badge, string Emoji, string Meaning)[] BadgeLegend =
    [
        (Badge.Trendsetter, "⚡", "first to bring it"),
        (Badge.NightOwl, "🦉", "night owl"),
        (Badge.RepeatKing, "🥱", "repeat king"),
    ];

    /// <summary>The ranking, best first, each line followed by its holder's badges; <paramref name="allTime"/> picks the header and hint.</summary>
    public static string Format(IReadOnlyList<(string Name, LeaderboardEntry Entry, IReadOnlyList<Badge> Badges)> ranking, bool allTime)
    {
        if (ranking.Count == 0)
        {
            return allTime
                ? "No reposts counted in this chat yet."
                : "No reposts in this chat in the last 30 days.";
        }

        var sb = new StringBuilder("🏆 Top link droppers — ").Append(Window(allTime)).Append("\n\n");
        for (var i = 0; i < ranking.Count; i++)
        {
            var (name, entry, badges) = ranking[i];
            sb.Append(i < Medals.Length ? Medals[i] : $"{i + 1}.")
                .Append(' ').Append(name)
                .Append(" — ").Append(entry.Reposts)
                .Append(" · ").Append(Favourite(entry, capitalised: false));
            if (badges.Count > 0)
            {
                sb.Append(' ').Append(string.Concat(badges.Select(EmojiOf)));
            }

            sb.Append('\n');
        }

        // Explain only the badges someone in the list actually holds.
        var shown = ranking.SelectMany(line => line.Badges).ToHashSet();
        var legend = BadgeLegend.Where(b => shown.Contains(b.Badge)).Select(b => $"{b.Emoji} {b.Meaning}").ToList();
        sb.Append('\n');
        if (legend.Count > 0)
        {
            sb.Append(string.Join(" · ", legend)).Append('\n');
        }

        sb.Append(allTime ? "/leaders — last 30 days" : "/leaders all — all time")
            .Append(" · /leaders optout — leave the ranking");
        return sb.ToString();
    }

    /// <summary>One person's <c>/me</c> card; <paramref name="stats"/> is null when nothing of theirs was reposted.</summary>
    public static string FormatMe(string name, PersonalStats? stats, bool allTime)
    {
        var sb = new StringBuilder("📇 ").Append(name).Append(" in this chat — ").Append(Window(allTime)).Append("\n\n");
        if (stats is null)
        {
            sb.Append("No links from you yet.");
            return sb.ToString();
        }

        sb.Append("🔗 ").Append(stats.Reposts).Append(stats.Reposts == 1 ? " link" : " links")
            .Append(" · #").Append(stats.Rank).Append(" of ").Append(stats.RankedPeople).Append('\n');
        sb.Append("📱 ").Append(Favourite(new LeaderboardEntry(0, stats.Reposts, stats.TopPlatform, stats.TopPlatformReposts), capitalised: true)).Append('\n');
        sb.Append("🕐 Most active around ").Append(stats.PeakHour.ToString("00", System.Globalization.CultureInfo.InvariantCulture)).Append(":00\n");
        if (stats.Trendsets > 0 || stats.Repeats > 0)
        {
            sb.Append("⚡ ").Append(stats.Trendsets).Append(stats.Trendsets == 1 ? " link" : " links").Append(" others posted again later")
                .Append(" · 🥱 ").Append(stats.Repeats).Append(stats.Repeats == 1 ? " repeat" : " repeats").Append('\n');
        }

        sb.Append('\n').Append(allTime ? "/me — last 30 days" : "/me all — all time");
        return sb.ToString();
    }

    private static string Window(bool allTime) => allTime ? "all time" : "last 30 days";

    private static string EmojiOf(Badge badge) => BadgeLegend.First(b => b.Badge == badge).Emoji;

    private static string Favourite(LeaderboardEntry entry, bool capitalised)
    {
        var text = entry.TopPlatformReposts == entry.Reposts
            ? $"all {entry.TopPlatform}"
            : $"mostly {entry.TopPlatform} ({entry.TopPlatformReposts})";
        return capitalised ? char.ToUpperInvariant(text[0]) + text[1..] : text;
    }
}
