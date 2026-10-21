using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LeBot.Infrastructure.MediaExtraction.Olx;

/// <summary>
/// Turns the HTML of an olx.ua advert page into an <see cref="OlxListing"/>. The page is a React app
/// that ships its first render as <c>window.__PRERENDERED_STATE__</c> — a JSON document wrapped in a
/// JavaScript string literal — and everything a caption needs (title, price, location, the category's
/// parameters, description, gallery) sits under <c>ad.ad</c> in it. So no markup is scraped and no
/// browser is needed.
/// </summary>
internal static partial class OlxListingParser
{
    // Telegram albums top out at 10 items.
    private const int MaxPhotos = 10;

    // A parameter earns a caption line when it reads at a glance ("Пробіг: 387 тис.км."). Longer values
    // are option lists or prose — a car's whole comfort package, a paint-condition essay — and would push
    // the seller's own description out of a 1024-char caption, so they stay on the page.
    private const int MaxParameterValueLength = 40;

    private const string StateMarker = "window.__PRERENDERED_STATE__";

    [GeneratedRegex(@"<br\s*/?>[ \t]*\r?\n?", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakTag();

    [GeneratedRegex(@"</p>\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ParagraphEnd();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"[ \t]+\n")]
    private static partial Regex TrailingSpaces();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLineRuns();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>
    /// Parse <paramref name="html"/> into a listing. Returns <c>null</c> when the page carries no advert —
    /// no state script, state that isn't JSON, or a state without a titled <c>ad.ad</c> (a job advert,
    /// a redirect to search). Individual missing fields come back null or empty.
    /// </summary>
    public static OlxListing? Parse(string html)
    {
        if (ReadState(html) is not { } json)
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            if (!TryGetObject(document.RootElement, "ad", out var page)
                || !TryGetObject(page, "ad", out var ad)
                || Line(ad, "title") is not { } title)
            {
                return null;
            }

            var (price, negotiable) = ReadPrice(ad);

            return new OlxListing(
                Title: title,
                Price: price,
                Negotiable: negotiable,
                Location: ReadLocation(ad),
                Parameters: ReadParameters(ad),
                Description: ReadDescription(ad),
                IsActive: !(ad.TryGetProperty("isActive", out var active) && active.ValueKind == JsonValueKind.False),
                PhotoUrls: ReadPhotos(ad));
        }
    }

    /// <summary>
    /// The JSON text inside the state's string literal, or null when the page has none. The literal is
    /// scanned by hand rather than matched by a regex: it runs to about a megabyte (the whole category
    /// tree rides along), and the only thing that ends it is the first unescaped quote.
    /// </summary>
    private static string? ReadState(string html)
    {
        var marker = html.IndexOf(StateMarker, StringComparison.Ordinal);
        if (marker < 0)
        {
            return null;
        }

        var afterMarker = marker + StateMarker.Length;
        var open = html.IndexOf('"', afterMarker);
        if (open < 0 || html.AsSpan(afterMarker, open - afterMarker).Trim() is not "=")
        {
            return null;
        }

        for (var i = open + 1; i < html.Length; i++)
        {
            if (html[i] == '\\')
            {
                i++;
            }
            else if (html[i] == '"')
            {
                try
                {
                    return JsonSerializer.Deserialize<string>(html.AsSpan(open, i - open + 1));
                }
                catch (JsonException)
                {
                    return null;
                }
            }
        }

        return null;
    }

    private static (string? Price, bool Negotiable) ReadPrice(JsonElement ad)
    {
        if (!TryGetObject(ad, "price", out var price))
        {
            return (null, false);
        }

        // "displayValue" is the page's own rendering — the seller's currency, or "Безкоштовно" /
        // "Обмін" — so it never needs formatting here. A free item has no regularPrice at all.
        var negotiable = TryGetObject(price, "regularPrice", out var regular)
            && regular.TryGetProperty("negotiable", out var flag)
            && flag.ValueKind == JsonValueKind.True;

        return (Line(price, "displayValue"), negotiable);
    }

    private static string? ReadLocation(JsonElement ad)
    {
        if (!TryGetObject(ad, "location", out var location))
        {
            return null;
        }

        string[] parts = [.. new[] { Line(location, "cityName"), Line(location, "districtName") }.OfType<string>()];
        return parts.Length > 0 ? string.Join(", ", parts) : Line(location, "regionName");
    }

    private static List<OlxParameter> ReadParameters(JsonElement ad)
    {
        var parameters = new List<OlxParameter>();
        if (!ad.TryGetProperty("params", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return parameters;
        }

        foreach (var item in list.EnumerateArray())
        {
            // Line() also drops a value that isn't a string — a salary range comes as an object.
            if (Line(item, "name") is { } name
                && Line(item, "value") is { Length: <= MaxParameterValueLength } value)
            {
                parameters.Add(new OlxParameter(name, value));
            }
        }

        return parameters;
    }

    /// <summary>The seller's text as plain lines: the page stores it as HTML, mostly <c>&lt;br /&gt;</c>-separated.</summary>
    private static string? ReadDescription(JsonElement ad)
    {
        if (!ad.TryGetProperty("description", out var value)
            || value.ValueKind != JsonValueKind.String
            || value.GetString() is not { } html)
        {
            return null;
        }

        var text = LineBreakTag().Replace(html, "\n");
        text = ParagraphEnd().Replace(text, "\n\n");
        text = AnyTag().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text).Replace("\r\n", "\n").Replace(' ', ' ');
        text = TrailingSpaces().Replace(text, "\n");
        text = BlankLineRuns().Replace(text, "\n\n").Trim();

        return text.Length > 0 ? text : null;
    }

    private static List<string> ReadPhotos(JsonElement ad)
    {
        var urls = new List<string>(MaxPhotos);
        if (!ad.TryGetProperty("photos", out var photos) || photos.ValueKind != JsonValueKind.Array)
        {
            return urls;
        }

        foreach (var photo in photos.EnumerateArray())
        {
            if (photo.ValueKind == JsonValueKind.String
                && photo.GetString() is { } url
                && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps
                && !urls.Contains(url))
            {
                urls.Add(url);
                if (urls.Count == MaxPhotos)
                {
                    break;
                }
            }
        }

        return urls;
    }

    private static bool TryGetObject(JsonElement parent, string property, out JsonElement value) =>
        parent.TryGetProperty(property, out value) && value.ValueKind == JsonValueKind.Object;

    /// <summary>A string property folded onto one line — runs of whitespace (OLX groups digits with NBSP) become one space — or null when absent or blank.</summary>
    private static string? Line(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var line = Whitespace().Replace(value.GetString() ?? string.Empty, " ").Trim();
        return line.Length > 0 ? line : null;
    }
}
