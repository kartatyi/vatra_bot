using System.Text;
using LeBot.Application.Telemetry;

namespace LeBot.Infrastructure.Telegram;

/// <summary>
/// The <c>/leaders</c> replies. Plain text with no parse mode, so a member's name can't inject formatting,
/// and names are written out rather than @-mentioned, so the ranking pings nobody.
/// </summary>
internal static class LeaderboardFormatter
{
    public const string OptedOut = "Done — you're out of the ranking in this chat. /leaders optin brings you back.";

    public const string OptedIn = "You're back in the ranking.";

    public const string NoSender = "Couldn't tell who you are — anonymous admins and channels can't opt out.";

    private static readonly string[] Medals = ["🥇", "🥈", "🥉"];

    /// <summary>The ranking, best first; <paramref name="allTime"/> picks the header and the footer's hint.</summary>
    public static string Format(IReadOnlyList<(string Name, LeaderboardEntry Entry)> ranking, bool allTime)
    {
        if (ranking.Count == 0)
        {
            return allTime
                ? "No reposts counted in this chat yet."
                : "No reposts in this chat in the last 30 days.";
        }

        var sb = new StringBuilder("🏆 Top link droppers — ").Append(allTime ? "all time" : "last 30 days").Append("\n\n");
        for (var i = 0; i < ranking.Count; i++)
        {
            var (name, entry) = ranking[i];
            sb.Append(i < Medals.Length ? Medals[i] : $"{i + 1}.")
                .Append(' ').Append(name)
                .Append(" — ").Append(entry.Reposts)
                .Append(" · ").Append(Favourite(entry))
                .Append('\n');
        }

        sb.Append('\n').Append(allTime ? "/leaders — last 30 days" : "/leaders all — all time")
            .Append(" · /leaders optout — leave the ranking");
        return sb.ToString();
    }

    private static string Favourite(LeaderboardEntry entry) =>
        entry.TopPlatformReposts == entry.Reposts
            ? $"all {entry.TopPlatform}"
            : $"mostly {entry.TopPlatform} ({entry.TopPlatformReposts})";
}
