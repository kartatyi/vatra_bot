using System.Globalization;
using System.Text;

namespace LeBot.Infrastructure.MediaExtraction.Steam;

/// <summary>
/// Formats a <see cref="SteamApp"/> into the plain-text caption that rides along with the trailer and
/// screenshots — plain because the messenger sends captions without a parse mode. The words all come
/// from the store, already in its language; the caption adds only emoji and punctuation, so it reads
/// the same whichever language the storefront is set to. A line whose fact is missing is left out.
/// </summary>
internal static class SteamCaptionBuilder
{
    private static readonly NumberFormatInfo GroupedByNoBreakSpace = new()
    {
        NumberGroupSeparator = " ",
        NumberGroupSizes = [3],
        NumberDecimalDigits = 0,
    };

    public static string Build(SteamApp app, SteamReviewSummary? reviews)
    {
        var facts = new List<string>();

        if (app.Price is { } price)
        {
            facts.Add("💵 " + FormatPrice(price));
        }

        if (app.ReleaseDate is { } releaseDate)
        {
            facts.Add((app.ComingSoon ? "⏳ " : "📅 ") + releaseDate);
        }

        if (app.Genres.Count > 0)
        {
            facts.Add("🏷 " + string.Join(" · ", app.Genres));
        }

        if (reviews is not null)
        {
            facts.Add($"⭐ {reviews.Verdict} — {reviews.PositivePercent}% · {reviews.TotalReviews.ToString("N", GroupedByNoBreakSpace)}");
        }

        var builder = new StringBuilder("🎮 ").Append(app.Name);

        if (facts.Count > 0)
        {
            builder.Append("\n\n").AppendJoin('\n', facts);
        }

        if (app.ShortDescription is { } description)
        {
            builder.Append("\n\n").Append(description);
        }

        return builder.ToString();
    }

    private static string FormatPrice(SteamPrice price) =>
        price.Initial is { } initial
            ? $"{initial} → {price.Final} (−{price.DiscountPercent}%)"
            : price.Final;
}
