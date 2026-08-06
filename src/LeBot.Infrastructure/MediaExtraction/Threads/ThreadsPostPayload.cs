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
    /// <remarks>
    /// A page describes the same post in more than one block — one carries the media, another the
    /// conversation it sits in — so the first block that mentions the shortcode is not necessarily
    /// the one that knows the post continues. Read them all and keep the best of each.
    /// </remarks>
    internal static ThreadsPost? FromHtml(string html, string shortcode)
    {
        ThreadsPost? post = null;
        IReadOnlyList<ThreadsPostPart> continuation = [];

        foreach (var block in JsonScriptBlock().Matches(html).Cast<Match>())
        {
            var json = block.Groups[1].Value;
            if (!json.Contains(shortcode, StringComparison.Ordinal))
            {
                continue;
            }

            var candidate = FromJson(json, shortcode);
            if (candidate is null)
            {
                continue;
            }

            if (post is null || (post.Media.Count == 0 && candidate.Media.Count > 0))
            {
                post = candidate;
            }

            if (continuation.Count == 0)
            {
                continuation = candidate.Continuation;
            }

            if (post.Media.Count > 0 && continuation.Count > 0)
            {
                break;
            }
        }

        return post is null ? null : post with { Continuation = continuation };
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
            if (!TryFindPost(document.RootElement, shortcode, depth: 0, out var node))
            {
                return null;
            }

            var post = Describe(node);
            return post with { Continuation = FindContinuation(document.RootElement, shortcode, post.Author) };
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

    private static ThreadsPost Describe(JsonElement post) =>
        new(Author: TryGetString(post, "user", "username"),
            Caption: TryGetString(post, "caption", "text"),
            Media: CollectMedia(post),
            Continuation: []);

    private static List<ThreadsMediaSource> CollectMedia(JsonElement post)
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

        return media;
    }

    /// <summary>
    /// The parts the author chained after the linked post. A Threads page is one long conversation:
    /// the author's own continuation, then everyone else's comments, then the author's replies to
    /// those comments — all in the same <c>edges</c> list. Only a post that is <em>by</em> the author
    /// <em>and</em> replies <em>to</em> the author continues the thread; the first item that isn't
    /// ends it, which is what keeps the comment section out of the chat.
    /// </summary>
    private static IReadOnlyList<ThreadsPostPart> FindContinuation(
        JsonElement root,
        string shortcode,
        string? author)
    {
        if (string.IsNullOrEmpty(author))
        {
            return [];
        }

        foreach (var edges in EnumerateThreadEdges(root, depth: 0))
        {
            var chain = ContinuationAfter(edges, shortcode, author);
            if (chain.Count > 0)
            {
                return chain;
            }
        }

        return [];
    }

    private static List<ThreadsPostPart> ContinuationAfter(JsonElement edges, string shortcode, string author)
    {
        var conversation = new List<JsonElement>();
        foreach (var edge in edges.EnumerateArray())
        {
            if (!edge.TryGetProperty("node", out var node)
                || !node.TryGetProperty("thread_items", out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("post", out var post))
                {
                    conversation.Add(post);
                }
            }
        }

        var linked = conversation.FindIndex(post => HasCode(post, shortcode));
        var parts = new List<ThreadsPostPart>();
        if (linked < 0)
        {
            return parts;
        }

        for (var i = linked + 1; i < conversation.Count && ContinuesTheThread(conversation[i], author); i++)
        {
            var text = TryGetString(conversation[i], "caption", "text");
            var media = CollectMedia(conversation[i]);
            if (text is not null || media.Count > 0)
            {
                parts.Add(new ThreadsPostPart(text, media));
            }
        }

        return parts;
    }

    private static IEnumerable<JsonElement> EnumerateThreadEdges(JsonElement element, int depth)
    {
        if (depth > MaxDepth)
        {
            yield break;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("edges", out var edges) && IsConversation(edges))
                {
                    yield return edges;
                }

                foreach (var property in element.EnumerateObject())
                {
                    foreach (var found in EnumerateThreadEdges(property.Value, depth + 1))
                    {
                        yield return found;
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                {
                    foreach (var found in EnumerateThreadEdges(child, depth + 1))
                    {
                        yield return found;
                    }
                }

                break;
        }
    }

    private static bool IsConversation(JsonElement edges) =>
        edges.ValueKind == JsonValueKind.Array
        && edges.GetArrayLength() > 0
        && edges[0].TryGetProperty("node", out var node)
        && node.TryGetProperty("thread_items", out _);

    private static bool HasCode(JsonElement post, string shortcode) =>
        post.TryGetProperty("code", out var code)
        && code.ValueKind == JsonValueKind.String
        && string.Equals(code.GetString(), shortcode, StringComparison.Ordinal);

    private static bool ContinuesTheThread(JsonElement post, string author) =>
        string.Equals(TryGetString(post, "user", "username"), author, StringComparison.Ordinal)
        && string.Equals(ReplyTarget(post), author, StringComparison.Ordinal);

    private static string? ReplyTarget(JsonElement post) =>
        post.TryGetProperty("text_post_app_info", out var info)
        && info.ValueKind == JsonValueKind.Object
            ? TryGetString(info, "reply_to_author", "username")
            : null;

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
