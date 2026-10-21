namespace LeBot.Infrastructure.MediaExtraction.Olx;

/// <summary>
/// The fields pulled out of an olx.ua advert page. Everything but <see cref="Title"/> is optional —
/// a free giveaway has no regular price, a village has no district, plenty of adverts carry no
/// parameters — so the caption builder simply skips whatever is missing.
/// </summary>
/// <param name="Title">The advert's headline as the seller wrote it.</param>
/// <param name="Price">The price exactly as the page shows it, e.g. "29 500 грн.", "3 999 $", "Безкоштовно".</param>
/// <param name="Negotiable">Whether the seller marked the price as open to haggling.</param>
/// <param name="Location">City and, when the page names one, district, e.g. "Київ, Дарницький".</param>
/// <param name="Parameters">The category's short facts in page order, e.g. ("Пробіг", "387 тис.км.").</param>
/// <param name="Description">The seller's text, converted from the page's HTML to plain lines.</param>
/// <param name="IsActive">False once the advert is closed or expired but the page still renders.</param>
/// <param name="PhotoUrls">Gallery photo URLs in page order, capped upstream.</param>
internal sealed record OlxListing(
    string Title,
    string? Price,
    bool Negotiable,
    string? Location,
    IReadOnlyList<OlxParameter> Parameters,
    string? Description,
    bool IsActive,
    IReadOnlyList<string> PhotoUrls);
