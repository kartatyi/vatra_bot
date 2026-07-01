using System.Text.Json;
using System.Text.RegularExpressions;

namespace LeBot.Infrastructure.MediaExtraction.AutoRia;

/// <summary>
/// Turns the server-rendered HTML of an auto.ria advert page into an <see cref="AutoRiaListing"/>.
/// auto.ria renders the whole advert server-side, so no browser or private API is needed. Two data
/// sources carry everything we want:
/// <list type="bullet">
/// <item>the <c>schema.org/Vehicle</c> and <c>BreadcrumbList</c> JSON-LD blocks — stable, standard,
/// and the backbone for title, mileage, transmission, colour, description and city;</item>
/// <item>the page's own Vue-state <c>TextTemplate</c> blocks (keyed by ids like
/// <c>descGenerationBaseValue</c>) — the only source for generation/trim and the richer engine line.</item>
/// </list>
/// Photos come from the gallery's <c>&lt;picture&gt;</c> tags; the "similar adverts" carousel is
/// excluded so recommended cars' photos never leak into the album.
/// </summary>
internal static partial class AutoRiaListingParser
{
    // Telegram albums top out at 10 items; keep the first ten gallery photos.
    private const int MaxPhotos = 10;

    // Every riastatic CDN host mirrors every photo, so we normalise to one host and ask for the
    // "hd" JPEG rendition (Telegram wants JPEG, and "hd" is a good size well under the upload cap).
    private const string PhotoCdnBase = "https://cdn.riastatic.com/photosnew/auto/photo/";

    [GeneratedRegex("""<script type="application/ld\+json">(.*?)</script>""", RegexOptions.Singleline)]
    private static partial Regex JsonLdBlock();

    [GeneratedRegex("""_(\d+)\.html""")]
    private static partial Regex AdvertId();

    [GeneratedRegex(""""content":"([^"]*)"""")]
    private static partial Regex FirstContent();

    // Captures a photo's base id (make_model__digits, hyphens allowed for makes like mercedes-benz),
    // dropping the size suffix and extension so any rendition URL collapses to one identity.
    [GeneratedRegex("""photosnew/auto/photo/([a-z0-9_-]+?_\d+)[a-z]+\.(?:webp|jpg)""")]
    private static partial Regex PhotoBase();

    /// <summary>
    /// Parse <paramref name="html"/> (the advert page at <paramref name="url"/>) into a listing.
    /// Returns <c>null</c> only when the page carries no Vehicle JSON-LD at all — i.e. it isn't an
    /// advert page (deleted, redirected to search, captcha). Individual missing fields come back null.
    /// </summary>
    public static AutoRiaListing? Parse(string html, Uri url)
    {
        var (vehicle, breadcrumb) = ReadJsonLd(html);
        if (vehicle is null)
        {
            return null;
        }

        var advertId = AdvertId().Match(url.AbsolutePath) is { Success: true } m ? m.Groups[1].Value : null;

        // Prefer the richer Vue-state lines where they exist, fall back to the JSON-LD standard fields.
        var engine = DescValue(html, "descEngineEngine") ?? vehicle.FuelType;
        var bodyType = DescValue(html, "descCharacteristicsValue") ?? vehicle.BodyType;

        return new AutoRiaListing(
            Title: vehicle.Name,
            Generation: DescValue(html, "descGenerationBaseValue"),
            Mileage: FormatMileage(vehicle.MileageKm),
            Gearbox: vehicle.Transmission,
            Engine: engine,
            DriveType: DescValue(html, "descDriveTypeDriveType"),
            Color: vehicle.Color,
            BodyType: bodyType,
            Location: breadcrumb,
            Price: ExtractPrice(html, advertId),
            Description: vehicle.Description,
            PhotoUrls: ExtractPhotos(html));
    }

    private static (VehicleData? Vehicle, string? City) ReadJsonLd(string html)
    {
        VehicleData? vehicle = null;
        string? city = null;

        foreach (Match block in JsonLdBlock().Matches(html))
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(block.Groups[1].Value);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("@type", out var type))
                {
                    continue;
                }

                var typeName = type.ValueKind == JsonValueKind.String ? type.GetString() : null;

                // The info block is the Vehicle that carries "name"; a second Vehicle block holds only
                // the image list and is ignored here (photos are read from the gallery markup instead).
                if (typeName == "Vehicle" && vehicle is null && root.TryGetProperty("name", out _))
                {
                    vehicle = ReadVehicle(root);
                }
                else if (typeName == "BreadcrumbList" && city is null)
                {
                    city = ReadCity(root);
                }
            }
        }

        return (vehicle, city);
    }

    private static VehicleData ReadVehicle(JsonElement root)
    {
        long mileage = 0;
        if (root.TryGetProperty("mileageFromOdometer", out var odo)
            && odo.TryGetProperty("value", out var value)
            && value.ValueKind == JsonValueKind.Number)
        {
            mileage = value.GetInt64();
        }

        return new VehicleData(
            Name: StringOrNull(root, "name"),
            Transmission: StringOrNull(root, "vehicleTransmission"),
            FuelType: StringOrNull(root, "fuelType"),
            Color: StringOrNull(root, "color"),
            BodyType: StringOrNull(root, "bodyType"),
            Description: StringOrNull(root, "description"),
            MileageKm: mileage);
    }

    private static string? ReadCity(JsonElement breadcrumb)
    {
        if (!breadcrumb.TryGetProperty("itemListElement", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        // The trail is home › category › region › city › brand › model › title. Both the city and the
        // brand/model crumbs carry "/city/" in their @id, but only the brand/model ones live under
        // "/car/" — so the city is the first "/city/" crumb that isn't a "/car/" link.
        foreach (var element in list.EnumerateArray())
        {
            if (!element.TryGetProperty("item", out var item)
                || !item.TryGetProperty("@id", out var idElement)
                || idElement.GetString() is not { } id)
            {
                continue;
            }

            if (id.Contains("/city/", StringComparison.Ordinal) && !id.Contains("/car/", StringComparison.Ordinal))
            {
                return StringOrNull(item, "name");
            }
        }

        return null;
    }

    /// <summary>Reads the first <c>"content"</c> value that follows a Vue-state block's id.</summary>
    private static string? DescValue(string html, string id)
    {
        var marker = $"\"id\":\"{id}\"";
        var index = html.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        // The value sits a few dozen chars after the id inside the same TextTemplate; a short window
        // keeps the search from ever spilling into the next block if this one happens to be empty.
        var length = Math.Min(600, html.Length - index);
        var match = FirstContent().Match(html, index, length);
        return match.Success ? Clean(match.Groups[1].Value) : null;
    }

    private static string? ExtractPrice(string html, string? advertId)
    {
        if (advertId is null)
        {
            return null;
        }

        // The advert's own price block sits immediately before its canonical link; anchoring on the
        // advert id skips the identical-looking price blocks of the "similar adverts" cards.
        var pattern = "\"prices\":\\{([^{}]*)\\},\"link\":\"[^\"]*_" + Regex.Escape(advertId) + "\\.html";
        var match = Regex.Match(html, pattern);
        if (!match.Success)
        {
            return null;
        }

        var body = match.Groups[1].Value;
        var usd = JsonField(body, "USD");
        var uah = JsonField(body, "UAH");

        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(usd))
        {
            parts.Add($"{usd} $");
        }

        if (!string.IsNullOrWhiteSpace(uah))
        {
            parts.Add($"{uah} ₴");
        }

        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }

    private static List<string> ExtractPhotos(string html)
    {
        var recommended = RecommendedPhotoBases(html);
        var urls = new List<string>(MaxPhotos);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in PhotoBase().Matches(html))
        {
            var baseId = match.Groups[1].Value;
            if (recommended.Contains(baseId) || !seen.Add(baseId))
            {
                continue;
            }

            urls.Add($"{PhotoCdnBase}{baseId}hd.jpg");
            if (urls.Count == MaxPhotos)
            {
                break;
            }
        }

        return urls;
    }

    /// <summary>Photo bases that belong to the "similar adverts" carousel, not this advert.</summary>
    private static HashSet<string> RecommendedPhotoBases(string html)
    {
        var bases = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        while ((index = html.IndexOf("\"id\":\"similarAdsCard", index, StringComparison.Ordinal)) >= 0)
        {
            // A card's photo src sits within a couple hundred chars of its id; a 2k window is ample.
            var length = Math.Min(2000, html.Length - index);
            foreach (Match match in PhotoBase().Matches(html.Substring(index, length)))
            {
                bases.Add(match.Groups[1].Value);
            }

            index += length;
        }

        return bases;
    }

    private static string? FormatMileage(long km)
    {
        if (km <= 0)
        {
            return null;
        }

        if (km >= 1000 && km % 1000 == 0)
        {
            return $"{km / 1000} тис. км";
        }

        return km >= 1000 ? $"{GroupThousands(km)} км" : $"{km} км";
    }

    private static string GroupThousands(long value)
    {
        // Space-grouped, culture-free ("107 500") to match auto.ria's own number style.
        var digits = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var groups = new List<string>();
        for (var end = digits.Length; end > 0; end -= 3)
        {
            var start = Math.Max(0, end - 3);
            groups.Insert(0, digits[start..end]);
        }

        return string.Join(' ', groups);
    }

    private static string? JsonField(string json, string name)
    {
        var match = Regex.Match(json, "\"" + Regex.Escape(name) + "\":\"([^\"]*)\"");
        if (!match.Success)
        {
            return null;
        }

        // auto.ria groups thousands with a non-breaking space ("53 000"); fold every whitespace
        // variant to a plain space so the caption is consistent ASCII.
        return Regex.Replace(match.Groups[1].Value, @"\s+", " ").Trim();
    }

    private static string? StringOrNull(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? Clean(string raw)
    {
        // Collapse runs of whitespace to a single space: auto.ria pads its bullet-separated lines
        // ("Кросовер  •  5 дверей") and groups numbers with non-breaking spaces.
        var cleaned = Regex.Replace(raw.Replace("\\/", "/"), @"\s+", " ").Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    private sealed record VehicleData(
        string? Name,
        string? Transmission,
        string? FuelType,
        string? Color,
        string? BodyType,
        string? Description,
        long MileageKm);
}
