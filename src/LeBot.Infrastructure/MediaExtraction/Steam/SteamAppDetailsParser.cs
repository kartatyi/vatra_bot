using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LeBot.Infrastructure.MediaExtraction.Steam;

/// <summary>
/// Parses the store's <c>api/appdetails</c> answer into a <see cref="SteamApp"/>. The endpoint is
/// undocumented but is what the store's own pages read. It answers HTTP 200 whether or not the app
/// exists — a delisted or region-locked app comes back as <c>"success": false</c> — so the body, not
/// the status, decides.
/// </summary>
internal static partial class SteamAppDetailsParser
{
    [GeneratedRegex("<[^>]*>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>
    /// The app <paramref name="json"/> describes, or null when the store holds no page for
    /// <paramref name="appId"/>. Throws <see cref="JsonException"/> on a body that isn't JSON at all.
    /// </summary>
    public static SteamApp? Parse(string json, int appId)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(appId.ToString(CultureInfo.InvariantCulture), out var entry)
            || entry.ValueKind != JsonValueKind.Object
            || !entry.TryGetProperty("success", out var success)
            || success.ValueKind != JsonValueKind.True
            || !entry.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object
            || CleanText(GetString(data, "name")) is not { } name)
        {
            return null;
        }

        var release = GetObject(data, "release_date");

        return new SteamApp(
            Name: name,
            ShortDescription: CleanText(GetString(data, "short_description")),
            Developer: ReadDevelopers(data),
            Price: ReadPrice(data),
            ReleaseDate: release is { } r ? CleanText(GetString(r, "date")) : null,
            ComingSoon: release is { } c && c.TryGetProperty("coming_soon", out var soon) && soon.ValueKind == JsonValueKind.True,
            Genres: ReadGenres(data),
            ScreenshotUrls: ReadScreenshots(data),
            HeaderImageUrl: ToHttpUri(GetString(data, "header_image")),
            TrailerManifestUrl: ReadTrailerManifest(data));
    }

    private static SteamPrice? ReadPrice(JsonElement data)
    {
        if (GetObject(data, "price_overview") is not { } price
            || CleanText(GetString(price, "final_formatted")) is not { } final)
        {
            return null;
        }

        var discount = price.TryGetProperty("discount_percent", out var percent) && percent.TryGetInt32(out var value)
            ? value
            : 0;

        // The store blanks initial_formatted when nothing is discounted; a stale value alongside a
        // zero discount would print a strikethrough price that isn't real.
        var initial = discount > 0 ? CleanText(GetString(price, "initial_formatted")) : null;

        return new SteamPrice(final, initial, initial is null ? 0 : discount);
    }

    private static string? ReadDevelopers(JsonElement data)
    {
        if (!data.TryGetProperty("developers", out var developers) || developers.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var names = developers.EnumerateArray()
            .Select(d => d.ValueKind == JsonValueKind.String ? CleanText(d.GetString()) : null)
            .OfType<string>()
            .ToList();

        return names.Count > 0 ? string.Join(", ", names) : null;
    }

    private static List<string> ReadGenres(JsonElement data)
    {
        if (!data.TryGetProperty("genres", out var genres) || genres.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return genres.EnumerateArray()
            .Select(g => g.ValueKind == JsonValueKind.Object ? CleanText(GetString(g, "description")) : null)
            .OfType<string>()
            .ToList();
    }

    private static List<Uri> ReadScreenshots(JsonElement data)
    {
        if (!data.TryGetProperty("screenshots", out var screenshots) || screenshots.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return screenshots.EnumerateArray()
            .Select(s => s.ValueKind == JsonValueKind.Object ? ToHttpUri(GetString(s, "path_full")) : null)
            .OfType<Uri>()
            .ToList();
    }

    /// <summary>
    /// The trailer the store page opens with: the first one flagged <c>highlight</c>, else the first
    /// listed. Only the H.264 manifest counts — the AV1 one doesn't play inline on every Telegram
    /// client, and HLS says nothing about duration, which the size budget needs.
    /// </summary>
    private static Uri? ReadTrailerManifest(JsonElement data)
    {
        if (!data.TryGetProperty("movies", out var movies) || movies.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return movies.EnumerateArray()
            .Where(m => m.ValueKind == JsonValueKind.Object)
            .OrderByDescending(m => m.TryGetProperty("highlight", out var h) && h.ValueKind == JsonValueKind.True)
            .Select(m => ToHttpUri(GetString(m, "dash_h264")))
            .OfType<Uri>()
            .FirstOrDefault();
    }

    private static JsonElement? GetObject(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static string? GetString(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Uri? ToHttpUri(string? candidate) =>
        Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
            ? uri
            : null;

    /// <summary>
    /// Store text arrives HTML-escaped (<c>&amp;quot;</c>) and now and then carries markup. Tags go
    /// first and entities second, so an escaped <c>&amp;lt;</c> survives as a literal bracket instead
    /// of turning into a tag that then gets stripped.
    /// </summary>
    private static string? CleanText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var cleaned = Whitespace().Replace(WebUtility.HtmlDecode(HtmlTag().Replace(text, " ")), " ").Trim();
        return cleaned.Length > 0 ? cleaned : null;
    }
}
