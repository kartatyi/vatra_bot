using System.Security.Cryptography;
using System.Text;

namespace LeBot.Infrastructure.MediaCache;

/// <summary>
/// Identity of a cached repost: the source URL reduced to the part that actually selects content,
/// plus a hash of that which is safe to use as a directory name.
/// </summary>
/// <param name="NormalizedUrl">The reduced URL. Stored in the entry so a hash collision can't serve the wrong video.</param>
/// <param name="DirectoryName">Hex digest of <paramref name="NormalizedUrl"/> — the entry's folder.</param>
internal sealed record MediaCacheKey(string NormalizedUrl, string DirectoryName)
{
    // Query params that identify the *sharer*, not the content: TikTok's share sheet appends
    // ?_r/?_t/?is_from_webapp, X appends ?s=&t=, Instagram ?igshid=. Two people posting the same clip
    // produce different URLs that differ only here — dropping them is what makes the cache hit at all
    // in a group chat. Anything not on this list is kept, because it might select the content
    // (?v= on YouTube being the obvious one).
    private static readonly HashSet<string> TrackingParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "igshid", "igsh", "fbclid", "gclid", "si", "s", "t", "_r", "_t", "is_from_webapp",
        "sender_device", "sender_web_id", "web_id", "share_app_id", "share_link_id", "share_item_id",
        "tt_from", "ref_src", "ref_url", "feature",
    };

    // 128 bits of SHA-256: collision-proof in practice for a cache that holds hundreds of entries,
    // and short enough to keep the path well clear of Windows' MAX_PATH.
    private const int DirectoryNameLength = 32;

    public static MediaCacheKey For(Uri url)
    {
        var host = url.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
        {
            host = host[4..];
        }

        // Scheme and fragment are dropped: http:// and https:// serve the same post, and #anchors
        // never change which video comes back. Path case is preserved — platform IDs are case-sensitive.
        var path = url.AbsolutePath.TrimEnd('/');
        var normalized = string.Concat(host, path.Length == 0 ? "/" : path, NormalizeQuery(url.Query));

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return new MediaCacheKey(normalized, Convert.ToHexStringLower(digest)[..DirectoryNameLength]);
    }

    private static string NormalizeQuery(string query)
    {
        if (query.Length <= 1)
        {
            return string.Empty;
        }

        var kept = query[1..]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(pair => !IsTracking(pair))
            .OrderBy(pair => pair, StringComparer.Ordinal)
            .ToList();

        return kept.Count == 0 ? string.Empty : "?" + string.Join('&', kept);
    }

    private static bool IsTracking(string pair)
    {
        var separator = pair.IndexOf('=', StringComparison.Ordinal);
        var name = separator < 0 ? pair : pair[..separator];
        return name.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) || TrackingParams.Contains(name);
    }
}
