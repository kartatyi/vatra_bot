using System.Globalization;
using System.Text.RegularExpressions;

namespace LeBot.Infrastructure.MediaExtraction.Steam;

/// <summary>
/// Reads the app id out of a link to a Steam store page: the page itself, its age gate, and the
/// <c>s.team/a/</c> short link the Steam client's share button produces. Bundles, packages, news
/// and community pages name something other than one app and are left alone.
/// </summary>
internal static partial class SteamAppUrl
{
    [GeneratedRegex(@"^/(?:agecheck/)?app/(?<id>\d{1,9})(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex StorePath();

    [GeneratedRegex(@"^/a/(?<id>\d{1,9})/?$")]
    private static partial Regex ShortLinkPath();

    /// <summary>The app id the link points at, or null when it isn't a link to one store page.</summary>
    public static int? TryGetAppId(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp)
        {
            return null;
        }

        var match = url.Host.ToLowerInvariant() switch
        {
            "store.steampowered.com" => StorePath().Match(url.AbsolutePath),
            "s.team" => ShortLinkPath().Match(url.AbsolutePath),
            _ => Match.Empty,
        };

        return match.Success
            && int.TryParse(match.Groups["id"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var appId)
            && appId > 0
                ? appId
                : null;
    }
}
