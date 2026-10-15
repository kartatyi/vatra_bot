namespace LeBot.Infrastructure.Configuration;

/// <summary>
/// Bound from the <c>Steam</c> section. Steam prices and localises every store answer per request, so
/// the extractor has to name the storefront it reads. Left to choose, the store API answers from
/// whichever region its edge cache last served: three links fetched a minute apart came back priced
/// in hryvnias, euros and rupees.
/// </summary>
public sealed class SteamOptions
{
    public const string SectionName = "Steam";

    /// <summary>Two-letter country code of the storefront whose prices the caption quotes.</summary>
    public string CountryCode { get; init; } = "ua";

    /// <summary>
    /// Steam's own language name (<c>english</c>, <c>ukrainian</c>, …). Localises the genres, the
    /// release date and the review verdict, and the description wherever the developer translated it.
    /// </summary>
    public string Language { get; init; } = "ukrainian";
}
