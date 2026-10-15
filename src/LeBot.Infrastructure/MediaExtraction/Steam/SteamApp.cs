namespace LeBot.Infrastructure.MediaExtraction.Steam;

/// <summary>
/// What the store API says about one app, reduced to what a repost shows. Every text field is
/// already in the storefront's language and free of HTML; every optional field is null or empty
/// when the store left it out.
/// </summary>
/// <param name="Name">The app's store title.</param>
/// <param name="ShortDescription">The one-paragraph pitch under the capsule.</param>
/// <param name="Developer">The developers, comma-joined.</param>
/// <param name="Price">Null for a free app and for one not on sale yet.</param>
/// <param name="ReleaseDate">The release date as the store prints it — a date, a quarter, or "coming soon".</param>
/// <param name="ComingSoon">True while the app is announced but not released.</param>
/// <param name="Genres">Store genres, in the store's order.</param>
/// <param name="ScreenshotUrls">Full-size screenshots, in the store's order.</param>
/// <param name="HeaderImageUrl">The wide capsule shown at the top of the store page.</param>
/// <param name="TrailerManifestUrl">DASH manifest of the H.264 trailer the store page leads with.</param>
internal sealed record SteamApp(
    string Name,
    string? ShortDescription,
    string? Developer,
    SteamPrice? Price,
    string? ReleaseDate,
    bool ComingSoon,
    IReadOnlyList<string> Genres,
    IReadOnlyList<Uri> ScreenshotUrls,
    Uri? HeaderImageUrl,
    Uri? TrailerManifestUrl);
