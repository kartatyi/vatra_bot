namespace LeBot.Application.Telemetry;

/// <summary>
/// Folds a journalled host into the platform a person would name: <c>vt.tiktok.com</c> and
/// <c>www.tiktok.com</c> are both TikTok, <c>youtu.be</c> is YouTube. Unknown hosts pass through
/// without their <c>www.</c>.
/// </summary>
public static class PlatformName
{
    private static readonly (string Name, string[] Domains)[] Known =
    [
        ("TikTok", ["tiktok.com"]),
        ("Instagram", ["instagram.com"]),
        ("YouTube", ["youtube.com", "youtu.be"]),
        ("Threads", ["threads.net", "threads.com"]),
        ("X", ["x.com", "twitter.com"]),
        ("Reddit", ["reddit.com", "redd.it"]),
        ("Facebook", ["facebook.com", "fb.watch"]),
        ("Steam", ["steampowered.com", "steamcommunity.com"]),
    ];

    /// <summary>The platform name for <paramref name="host"/>.</summary>
    public static string Of(string host)
    {
        foreach (var (name, domains) in Known)
        {
            if (domains.Any(domain => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal)))
            {
                return name;
            }
        }

        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }
}
