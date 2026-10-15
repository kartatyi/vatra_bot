using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace LeBot.Infrastructure.MediaExtraction.Steam;

/// <summary>
/// The parts of a Steam trailer's DASH manifest that decide what to download: how long the trailer
/// runs, and how many bits per second each H.264 rendition and the audio track spend. Steam publishes
/// trailers only as DASH/HLS — separate video and audio streams, no progressive mp4 — so the manifest
/// is the one place a trailer's size is knowable before any of it is downloaded.
/// </summary>
/// <param name="Duration">Running time of the whole trailer.</param>
/// <param name="AudioBandwidth">Bits per second of the audio track; 0 for a silent trailer.</param>
/// <param name="Renditions">Every H.264 video rendition on offer.</param>
internal sealed record SteamTrailerManifest(
    TimeSpan Duration,
    long AudioBandwidth,
    IReadOnlyList<SteamTrailerManifest.Rendition> Renditions)
{
    // Declared bandwidth came within 3 % of a real 720p download (2.79 Mbit/s declared, 2.71 measured).
    // The margin covers the mp4 container and a busier trailer running closer to its ceiling.
    private const double SizeMargin = 1.1;

    /// <summary>One video rendition: its height in pixels and its declared bits per second.</summary>
    public sealed record Rendition(int Height, long Bandwidth);

    /// <summary>Predicted size of the merged mp4 for <paramref name="rendition"/> plus the audio track.</summary>
    public long EstimateBytes(Rendition rendition) =>
        (long)((rendition.Bandwidth + AudioBandwidth) / 8d * Duration.TotalSeconds * SizeMargin);

    /// <summary>
    /// The tallest rendition no taller than <paramref name="maxHeight"/> whose predicted size fits
    /// <paramref name="budgetBytes"/>; null when even the smallest runs over — a trailer that long
    /// is better skipped than posted as a smear.
    /// </summary>
    public Rendition? PickRendition(long budgetBytes, int maxHeight) =>
        Renditions
            .Where(r => r.Height <= maxHeight && EstimateBytes(r) <= budgetBytes)
            .OrderByDescending(r => r.Height)
            .ThenBy(r => r.Bandwidth)
            .FirstOrDefault();

    /// <summary>
    /// The manifest in <paramref name="mpd"/>, or null when it names no duration or no H.264 video.
    /// Throws <see cref="XmlException"/> on a body that isn't XML at all.
    /// </summary>
    public static SteamTrailerManifest? Parse(string mpd)
    {
        var root = XDocument.Parse(mpd).Root;
        if (root is null
            || root.Name.LocalName != "MPD"
            || ReadDuration((string?)root.Attribute("mediaPresentationDuration")) is not { } duration)
        {
            return null;
        }

        var renditions = new List<Rendition>();
        long audioBandwidth = 0;

        foreach (var set in root.Descendants().Where(e => e.Name.LocalName == "AdaptationSet"))
        {
            foreach (var representation in set.Elements().Where(e => e.Name.LocalName == "Representation"))
            {
                if (ReadNumber(representation, "bandwidth") is not { } bandwidth)
                {
                    continue;
                }

                switch (ContentType(set, representation))
                {
                    case "video" when IsH264(set, representation)
                        && ReadNumber(representation, "height") is { } height:
                        renditions.Add(new Rendition((int)height, bandwidth));
                        break;
                    case "audio":
                        audioBandwidth = Math.Max(audioBandwidth, bandwidth);
                        break;
                }
            }
        }

        return renditions.Count > 0 ? new SteamTrailerManifest(duration, audioBandwidth, renditions) : null;
    }

    private static TimeSpan? ReadDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var duration = XmlConvert.ToTimeSpan(value);
            return duration > TimeSpan.Zero ? duration : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>contentType</c> sits on the adaptation set in Steam's manifests; the DASH spec equally
    /// allows only a <c>mimeType</c>, on either element, so that is the fallback.
    /// </summary>
    private static string? ContentType(XElement set, XElement representation)
    {
        var declared = (string?)set.Attribute("contentType");
        if (!string.IsNullOrEmpty(declared))
        {
            return declared.ToLowerInvariant();
        }

        var mimeType = (string?)representation.Attribute("mimeType") ?? (string?)set.Attribute("mimeType");
        return mimeType?.Split('/', 2)[0].ToLowerInvariant();
    }

    // A rendition that names its codec must name H.264 — the only one every Telegram client plays
    // inline. One that names none is taken at the manifest's word: this is the dash_h264 manifest.
    private static bool IsH264(XElement set, XElement representation)
    {
        var codecs = (string?)representation.Attribute("codecs") ?? (string?)set.Attribute("codecs");
        return string.IsNullOrEmpty(codecs)
            || codecs.StartsWith("avc1", StringComparison.OrdinalIgnoreCase)
            || codecs.StartsWith("avc3", StringComparison.OrdinalIgnoreCase);
    }

    private static long? ReadNumber(XElement element, string attribute) =>
        long.TryParse((string?)element.Attribute(attribute), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && value > 0
                ? value
                : null;
}
