namespace LeBot.Infrastructure.MediaExtraction.Threads;

/// <summary>
/// The URL rule both Threads extractors share. It lived twice — once per extractor — which is how
/// <c>/share/&lt;code&gt;</c> shortlinks slipped past both: the share form 302s to the canonical
/// <c>/@user/post/&lt;code&gt;</c> URL, so every client downstream (HttpClient follows redirects by
/// default, headless Chrome likewise) already lands on a real post. Only the gate had to learn the
/// shape exists.
/// </summary>
internal static class ThreadsUrl
{
    /// <summary>
    /// True for a Threads post URL — the canonical <c>/@user/post/&lt;code&gt;</c> form and the
    /// <c>/share/&lt;code&gt;</c> shortlink the mobile app hands out.
    /// </summary>
    internal static bool IsPost(Uri url)
    {
        var host = url.Host;
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            host = host[4..];
        }

        if (!host.Equals("threads.com", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("threads.net", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = url.AbsolutePath;
        return path.Contains("/post/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/share/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The post's shortcode — the <c>DbsHKtBiGxC</c> in <c>/@user/post/DbsHKtBiGxC</c>. Null for a
    /// <c>/share/</c> shortlink, which only names its post once the redirect has been followed.
    /// </summary>
    internal static string? Shortcode(Uri url)
    {
        var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("post", StringComparison.OrdinalIgnoreCase)
                && IsShortcodeShaped(segments[i + 1]))
            {
                return segments[i + 1];
            }
        }

        return null;
    }

    // Shortcodes are base64url alphabet. Checking the shape here is what makes the value safe to
    // inline into the page-side script the browser fallback evaluates.
    private static bool IsShortcodeShaped(string segment) =>
        segment.Length is > 0 and <= 32
        && segment.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
