namespace LeBot.Infrastructure.MediaExtraction.Steam;

/// <summary>A store price, already formatted in the storefront's currency.</summary>
/// <param name="Final">What the app costs right now.</param>
/// <param name="Initial">The pre-discount price; null when there is no discount.</param>
/// <param name="DiscountPercent">0 when there is no discount.</param>
internal sealed record SteamPrice(string Final, string? Initial, int DiscountPercent);
