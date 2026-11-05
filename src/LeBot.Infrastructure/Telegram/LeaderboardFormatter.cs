using System.Globalization;
using System.Text;
using LeBot.Application.Telemetry;

namespace LeBot.Infrastructure.Telegram;

/// <summary>
/// The <c>/leaders</c> and <c>/me</c> replies, in the language of whoever asked. Plain text with no parse
/// mode, so a member's name can't inject formatting, and names are written out rather than @-mentioned, so
/// the ranking pings nobody.
/// </summary>
internal static class LeaderboardFormatter
{
    private static readonly string[] Medals = ["🥇", "🥈", "🥉"];

    private static readonly (Badge Badge, string Emoji, string Uk, string En)[] BadgeLegend =
    [
        (Badge.Trendsetter, "⚡", "першим приносить", "first to bring it"),
        (Badge.NightOwl, "🦉", "нічна сова", "night owl"),
        (Badge.RepeatKing, "🥱", "король баянів", "repeat king"),
    ];

    public static string OptedOut(BotLanguage lang) => lang.Pick(
        "Готово — тебе прибрано з рейтингу цього чату. /leaders optin поверне.",
        "Done — you're out of the ranking in this chat. /leaders optin brings you back.");

    public static string StatsOff(BotLanguage lang) => lang.Pick(
        "Статистику в цьому чаті вимкнено — увімкнути можна в /settings.",
        "Stats are off in this chat — /settings turns them on.");

    public static string OptedIn(BotLanguage lang) => lang.Pick("Ти знову в рейтингу.", "You're back in the ranking.");

    public static string NoSender(BotLanguage lang) => lang.Pick(
        "Не можу визначити, хто ти — анонімні адміни й канали не можуть вийти з рейтингу.",
        "Couldn't tell who you are — anonymous admins and channels can't opt out.");

    public static string MeOptedOut(BotLanguage lang) => lang.Pick(
        "Тебе немає в рейтингу цього чату — /leaders optin, щоб знову бачити свою статистику.",
        "You're out of the ranking in this chat — /leaders optin to see your stats again.");

    /// <summary>The ranking, best first, each line followed by its holder's badges; <paramref name="allTime"/> picks the header and hint.</summary>
    public static string Format(
        IReadOnlyList<(string Name, LeaderboardEntry Entry, IReadOnlyList<Badge> Badges)> ranking,
        bool allTime,
        BotLanguage lang = BotLanguage.En)
    {
        if (ranking.Count == 0)
        {
            return allTime
                ? lang.Pick("У цьому чаті ще немає порахованих репостів.", "No reposts counted in this chat yet.")
                : lang.Pick("У цьому чаті за останні 30 днів репостів не було.", "No reposts in this chat in the last 30 days.");
        }

        var sb = new StringBuilder(lang.Pick("🏆 Хто кидає найбільше лінків — ", "🏆 Top link droppers — "))
            .Append(Window(allTime, lang)).Append("\n\n");
        for (var i = 0; i < ranking.Count; i++)
        {
            var (name, entry, badges) = ranking[i];
            sb.Append(i < Medals.Length ? Medals[i] : $"{i + 1}.")
                .Append(' ').Append(name)
                .Append(" — ").Append(entry.Reposts)
                .Append(" · ").Append(Favourite(entry, capitalised: false, lang));
            if (badges.Count > 0)
            {
                sb.Append(' ').Append(string.Concat(badges.Select(EmojiOf)));
            }

            sb.Append('\n');
        }

        // Explain only the badges someone in the list actually holds.
        var shown = ranking.SelectMany(line => line.Badges).ToHashSet();
        var legend = BadgeLegend.Where(b => shown.Contains(b.Badge)).Select(b => $"{b.Emoji} {lang.Pick(b.Uk, b.En)}").ToList();
        sb.Append('\n');
        if (legend.Count > 0)
        {
            sb.Append(string.Join(" · ", legend)).Append('\n');
        }

        sb.Append(allTime
                ? lang.Pick("/leaders — останні 30 днів", "/leaders — last 30 days")
                : lang.Pick("/leaders all — за весь час", "/leaders all — all time"))
            .Append(lang.Pick(" · /leaders optout — вийти з рейтингу", " · /leaders optout — leave the ranking"));
        return sb.ToString();
    }

    /// <summary>One person's <c>/me</c> card; <paramref name="stats"/> is null when nothing of theirs was reposted.</summary>
    public static string FormatMe(string name, PersonalStats? stats, bool allTime, BotLanguage lang = BotLanguage.En)
    {
        var sb = new StringBuilder("📇 ").Append(name).Append(lang.Pick(" у цьому чаті — ", " in this chat — "))
            .Append(Window(allTime, lang)).Append("\n\n");
        if (stats is null)
        {
            sb.Append(lang.Pick("Від тебе ще немає лінків.", "No links from you yet."));
            return sb.ToString();
        }

        sb.Append("🔗 ").Append(lang.Count(stats.Reposts, "лінк", "лінки", "лінків", "link", "links"))
            .Append(" · #").Append(stats.Rank).Append(lang.Pick(" з ", " of ")).Append(stats.RankedPeople).Append('\n');
        sb.Append("📱 ").Append(Favourite(new LeaderboardEntry(0, stats.Reposts, stats.TopPlatform, stats.TopPlatformReposts), capitalised: true, lang)).Append('\n');
        sb.Append(lang.Pick("🕐 Найактивніше близько ", "🕐 Most active around "))
            .Append(stats.PeakHour.ToString("00", CultureInfo.InvariantCulture)).Append(":00\n");
        if (stats.Trendsets > 0 || stats.Repeats > 0)
        {
            sb.Append("⚡ ").Append(lang.Count(stats.Trendsets, "лінк", "лінки", "лінків", "link", "links"))
                .Append(lang.Pick(", які інші потім кинули знову", " others posted again later"))
                .Append(" · 🥱 ").Append(lang.Count(stats.Repeats, "повтор", "повтори", "повторів", "repeat", "repeats")).Append('\n');
        }

        sb.Append('\n').Append(allTime
            ? lang.Pick("/me — останні 30 днів", "/me — last 30 days")
            : lang.Pick("/me all — за весь час", "/me all — all time"));
        return sb.ToString();
    }

    private static string Window(bool allTime, BotLanguage lang) =>
        allTime ? lang.Pick("за весь час", "all time") : lang.Pick("останні 30 днів", "last 30 days");

    private static string EmojiOf(Badge badge) => BadgeLegend.First(b => b.Badge == badge).Emoji;

    private static string Favourite(LeaderboardEntry entry, bool capitalised, BotLanguage lang)
    {
        var text = entry.TopPlatformReposts == entry.Reposts
            ? lang.Pick($"лише {entry.TopPlatform}", $"all {entry.TopPlatform}")
            : lang.Pick($"здебільшого {entry.TopPlatform} ({entry.TopPlatformReposts})", $"mostly {entry.TopPlatform} ({entry.TopPlatformReposts})");
        return capitalised ? char.ToUpperInvariant(text[0]) + text[1..] : text;
    }
}
