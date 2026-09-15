using System.Text.Json;

namespace LeBot.Infrastructure.MediaExtraction.Steam;

/// <summary>The store's review verdict for an app, as its <c>appreviews</c> endpoint summarises it.</summary>
/// <param name="Verdict">The localised label — "Very Positive", "Mixed", …</param>
/// <param name="TotalPositive">How many of the reviews recommend the app.</param>
/// <param name="TotalReviews">How many reviews the verdict rests on.</param>
internal sealed record SteamReviewSummary(string Verdict, int TotalPositive, int TotalReviews)
{
    /// <summary>The recommending share, rounded down — 100 % only when no review is negative.</summary>
    public int PositivePercent => TotalReviews > 0 ? (int)(TotalPositive * 100L / TotalReviews) : 0;

    /// <summary>
    /// The summary inside an <c>appreviews</c> answer, or null when there is no verdict to show.
    /// An app with too few reviews still gets a label ("3 user reviews") but a <c>review_score</c> of
    /// 0 — that label restates a count, not an opinion, so it is dropped. Throws
    /// <see cref="JsonException"/> on a body that isn't JSON at all.
    /// </summary>
    public static SteamReviewSummary? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("query_summary", out var summary)
            || summary.ValueKind != JsonValueKind.Object
            || !TryGetInt(summary, "review_score", out var score) || score <= 0
            || !TryGetInt(summary, "total_positive", out var positive)
            || !TryGetInt(summary, "total_reviews", out var total) || total <= 0
            || !summary.TryGetProperty("review_score_desc", out var desc)
            || desc.ValueKind != JsonValueKind.String
            || desc.GetString() is not { } verdict
            || string.IsNullOrWhiteSpace(verdict))
        {
            return null;
        }

        return new SteamReviewSummary(verdict.Trim(), positive, total);
    }

    private static bool TryGetInt(JsonElement parent, string property, out int value)
    {
        value = 0;
        return parent.TryGetProperty(property, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out value);
    }
}
