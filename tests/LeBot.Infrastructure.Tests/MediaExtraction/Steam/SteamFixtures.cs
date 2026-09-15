namespace LeBot.Infrastructure.Tests.MediaExtraction.Steam;

/// <summary>
/// Store answers captured live on 2026-09-15 from the Ukrainian storefront (<c>cc=ua&amp;l=ukrainian</c>).
/// </summary>
internal static class SteamFixtures
{
    /// <summary>RV There Yet? — discounted, three highlighted trailers, ten screenshots.</summary>
    public const int DiscountedAppId = 3949040;

    /// <summary>An unreleased card game — no price, "coming soon", an HTML-escaped description.</summary>
    public const int ComingSoonAppId = 4506090;

    public static string AppDetails(int appId) => Load($"appdetails-{appId}.json");

    public static string Reviews() => Load("appreviews-3949040.json");

    /// <summary>The DASH manifest of YAPYAP's release trailer: 81.8 s, 1080/720/480/360p H.264 plus AAC.</summary>
    public static string TrailerManifest() => Load("trailer-3834090.mpd");

    private static string Load(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "_fixtures", "steam", fileName));
}
