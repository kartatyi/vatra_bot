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
}
