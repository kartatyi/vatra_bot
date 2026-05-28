using System.Text.RegularExpressions;
using LeBot.Application.Ports;

namespace LeBot.Infrastructure.Text;

/// <summary>
/// Pulls http/https URLs out of free-form text using a single greedy regex,
/// trims trailing punctuation that humans usually intend to be outside the URL,
/// and deduplicates by canonical form.
/// </summary>
public sealed partial class RegexUrlExtractor : IUrlExtractor
{
    private static readonly char[] TrailingPunctuation =
        ['.', ',', ';', ':', '!', '?', ')', ']', '}', '\'', '"', '>', '»', '`'];

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex UrlPattern();

    public IReadOnlyList<Uri> Extract(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var matches = UrlPattern().Matches(text);
        if (matches.Count == 0)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Uri>(matches.Count);

        foreach (Match match in matches)
        {
            var candidate = match.Value.TrimEnd(TrailingPunctuation);

            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
            {
                continue;
            }

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                continue;
            }

            if (seen.Add(uri.AbsoluteUri))
            {
                result.Add(uri);
            }
        }

        return result;
    }
}
