using System.Globalization;
using System.Text;
using LeBot.Application.Digest;

namespace LeBot.Infrastructure.Telegram;

/// <summary>
/// The weekly digest post. Plain text, names written out rather than @-mentioned, so it pings nobody. It goes
/// to the whole chat, so it's in the default language unless told otherwise.
/// </summary>
internal static class DigestFormatter
{
    private static readonly string[] Medals = ["🥇", "🥈", "🥉"];

    // Genitive month names: "6–12 жовтня", "29 вересня – 5 жовтня".
    private static readonly string[] UkMonths =
        ["січня", "лютого", "березня", "квітня", "травня", "червня", "липня", "серпня", "вересня", "жовтня", "листопада", "грудня"];

    /// <summary>
    /// The post for <paramref name="digest"/>; <paramref name="names"/> holds the top droppers who could be
    /// named, best first — someone who left the chat is simply missing from it.
    /// </summary>
    public static string Format(WeeklyDigest digest, IReadOnlyList<(string Name, int Reposts)> names, BotLanguage lang = BotLanguage.En)
    {
        var sb = new StringBuilder(lang.Pick("📬 Тиждень у лінках · ", "📬 Week in links · "))
            .Append(Range(digest.WeekStart, digest.WeekEnd, lang)).Append("\n\n");
        sb.Append("🔗 ").Append(lang.Pick(
                $"Переслано: {lang.Count(digest.Reposts, "лінк", "лінки", "лінків", "link", "links")}",
                $"{lang.Count(digest.Reposts, "лінк", "лінки", "лінків", "link", "links")} reposted"))
            .Append(" (").Append(Change(digest.Change, lang)).Append(")\n");
        sb.Append(lang.Pick("📱 Топ платформа: ", "📱 Top platform: ")).Append(digest.TopPlatform).Append(" — ").Append(digest.TopPlatformReposts).Append('\n');
        if (names.Count > 0)
        {
            sb.Append(lang.Pick("🏆 Найбільше лінків: ", "🏆 Top droppers: "))
                .Append(string.Join(" · ", names.Select((n, i) => $"{Medals[i]} {n.Name} {n.Reposts}")))
                .Append('\n');
        }

        sb.Append(lang.Pick(
            "\n/leaders — повний рейтинг · /settings — вимкнути підсумок",
            "\n/leaders for the full ranking · /settings to turn the digest off"));
        return sb.ToString();
    }

    private static string Change(int change, BotLanguage lang) => change switch
    {
        > 0 => lang.Pick($"↑{change} порівняно з минулим тижнем", $"↑{change} vs last week"),
        < 0 => lang.Pick($"↓{-change} порівняно з минулим тижнем", $"↓{-change} vs last week"),
        _ => lang.Pick("як і минулого тижня", "same as last week"),
    };

    /// <summary>"Oct 6–12" / "6–12 жовтня" within a month; "Sep 29 – Oct 5" / "29 вересня – 5 жовтня" across two.</summary>
    private static string Range(DateOnly start, DateOnly end, BotLanguage lang)
    {
        if (lang == BotLanguage.Uk)
        {
            return start.Month == end.Month
                ? $"{start.Day}–{end.Day} {UkMonths[end.Month - 1]}"
                : $"{start.Day} {UkMonths[start.Month - 1]} – {end.Day} {UkMonths[end.Month - 1]}";
        }

        var culture = CultureInfo.InvariantCulture;
        return start.Month == end.Month
            ? $"{start.ToString("MMM d", culture)}–{end.Day}"
            : $"{start.ToString("MMM d", culture)} – {end.ToString("MMM d", culture)}";
    }
}
