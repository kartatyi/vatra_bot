using System.Text.Json;
using System.Text.RegularExpressions;
using LeBot.Domain.Media;

namespace LeBot.Infrastructure.MediaExtraction.Threads;

/// <summary>
/// Reads the post description Threads ships inside its own page. Every Threads page embeds the
/// GraphQL result it was rendered from in <c>&lt;script type="application/json"&gt;</c> blocks — the
/// same Instagram-shaped node (<c>carousel_media</c> / <c>image_versions2</c> / <c>video_versions</c>)
/// the sibling Instagram extractor already reads. That payload is the only description of the post
/// that is actually *about the post*: the crawler-visible <c>og:image</c> is a rendered social card
/// (author header, body text as pixels, a collage strip of a carousel), and the DOM's first
/// <c>&lt;video&gt;</c> may belong to the recommendation feed further down the page.
///
/// A page carries many posts (replies, recommendations), so the node is matched on the post's own
/// shortcode — never "the first media-looking thing on the page".
/// </summary>
internal static partial class ThreadsPostPayload
{
    // Guards against a pathological payload spinning the walk; real nodes sit ~25 levels deep.
    private const int MaxDepth = 60;

    [GeneratedRegex(
        """<script type="application/json"[^>]*>(.*?)</script>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex JsonScriptBlock();

    /// <summary>Finds the post inside a full page. Returns null when the page carries no payload for it.</summary>
    internal static ThreadsPost? FromHtml(string html, string shortcode)
    {
        foreach (var block in JsonScriptBlock().Matches(html).Cast<Match>())
        {
            var json = block.Groups[1].Value;
            if (!json.Contains(shortcode, StringComparison.Ordinal))
            {
                continue;
            }

            var post = FromJson(json, shortcode);
            if (post is not null)
            {
                return post;
            }
        }

        return null;
    }

    /// <summary>Same walk over a single already-isolated payload block.</summary>
    internal static ThreadsPost? FromJson(string json, string shortcode)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            // Not every application/json block on the page is a Relay payload; skip the odd ones out.
            return null;
        }

        using (document)
        {
            return TryFindPost(document.RootElement, shortcode, depth: 0, out var node)
                ? Describe(node)
                : null;
        }
    }

    private static bool TryFindPost(JsonElement element, string shortcode, int depth, out JsonElement post)
    {
        post = default;
        if (depth > MaxDepth)
        {
            return false;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (IsPostNode(element, shortcode))
                {
                    post = element;
                    return true;
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (TryFindPost(property.Value, shortcode, depth + 1, out post))
                    {
                        return true;
                    }
                }

                return false;

            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                {
                    if (TryFindPost(child, shortcode, depth + 1, out post))
                    {
                        return true;
                    }
                }

                return false;

            default:
                return false;
        }
    }

    // The post's shortcode alone isn't enough: lightweight references to the same post (share
    // sheets, logging blobs) carry the code with no media. Require a media-bearing node.
    private static bool IsPostNode(JsonElement element, string shortcode) =>
        element.TryGetProperty("code", out var code)
        && code.ValueKind == JsonValueKind.String
        && string.Equals(code.GetString(), shortcode, StringComparison.Ordinal)
        && (element.TryGetProperty("carousel_media", out _)
            || element.TryGetProperty("image_versions2", out _)
            || element.TryGetProperty("video_versions", out _));

    private static ThreadsPost Describe(JsonElement post)
    {
        var media = new List<ThreadsMediaSource>();
        if (post.TryGetProperty("carousel_media", out var carousel)
            && carousel.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in carousel.EnumerateArray())
            {
                AddSource(child, media);
            }
        }

        // A single-attachment post carries its media on the node itself; a carousel node repeats its
        // first child's image there, so only look when the carousel yielded nothing.
        if (media.Count == 0)
        {
            AddSource(post, media);
        }

        return new ThreadsPost(
            Author: TryGetString(post, "user", "username"),
            Caption: TryGetString(post, "caption", "text"),
            Media: media);
    }

    private static void AddSource(JsonElement node, List<ThreadsMediaSource> media)
    {
        // Presence of a video rendition decides the kind: it handles image and video carousel
        // children uniformly and survives a missing or unfamiliar media_type tag.
        if (TryBestVideo(node, out var videoUrl))
        {
            media.Add(new ThreadsMediaSource(videoUrl, MediaKind.Video));
            return;
        }

        if (TryBestImage(node, out var imageUrl))
        {
            media.Add(new ThreadsMediaSource(imageUrl, MediaKind.Photo));
        }
    }

    private static bool TryBestVideo(JsonElement node, out string url) =>
        TryLargest(node.TryGetProperty("video_versions", out var versions) ? versions : default, out url);

    private static bool TryBestImage(JsonElement node, out string url) =>
        TryLargest(
            node.TryGetProperty("image_versions2", out var versions)
                && versions.TryGetProperty("candidates", out var candidates)
                ? candidates
                : default,
            out url);

    /// <summary>
    /// Picks the highest-resolution rendition. Image candidates carry <c>width</c>/<c>height</c> and
    /// are usually ordered largest-first — but the cost of trusting that and being wrong is
    /// reposting a 240px thumbnail, so measure instead. Video renditions carry no dimensions at all
    /// (just <c>type</c> 101/102/103 of the same clip), so there the first one wins, which is the
    /// progressive MP4 Meta lists first.
    /// </summary>
    private static bool TryLargest(JsonElement renditions, out string url)
    {
        url = string.Empty;
        if (renditions.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        long bestPixels = -1;
        foreach (var rendition in renditions.EnumerateArray())
        {
            if (rendition.ValueKind != JsonValueKind.Object
                || !rendition.TryGetProperty("url", out var candidate)
                || candidate.GetString() is not { Length: > 0 } candidateUrl)
            {
                continue;
            }

            var pixels = (long)Dimension(rendition, "width") * Dimension(rendition, "height");
            if (pixels > bestPixels)
            {
                bestPixels = pixels;
                url = candidateUrl;
            }
        }

        return url.Length > 0;
    }

    private static int Dimension(JsonElement rendition, string name) =>
        rendition.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var size)
            ? size
            : 0;

    private static string? TryGetString(JsonElement parent, string objectName, string property) =>
        parent.TryGetProperty(objectName, out var child)
        && child.ValueKind == JsonValueKind.Object
        && child.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
            ? text
            : null;
}
