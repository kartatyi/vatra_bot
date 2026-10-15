using System.Text.RegularExpressions;

namespace LeBot.Domain.Settings;

/// <summary>
/// The <see cref="CaptionMode.Brief"/> transform: what's left of a caption once the platform noise is
/// gone. Hashtags and @mentions go wherever they stand, blank runs collapse, and the rest is cut at a
/// word boundary near <see cref="MaxLength"/>.
/// </summary>
public static partial class CaptionShortener
{
    /// <summary>The cut-off before the ellipsis — about three lines on a phone.</summary>
    public const int MaxLength = 200;

    /// <summary>Returns the shortened text, or <see langword="null"/> when nothing but noise was there.</summary>
    public static string? Shorten(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var stripped = Noise().Replace(text, string.Empty);
        var lines = stripped
            .Split('\n')
            .Select(line => Spaces().Replace(line, " ").Trim())
            .Where(line => line.Length > 0);
        var compact = string.Join('\n', lines);

        if (compact.Length == 0)
        {
            return null;
        }

        if (compact.Length <= MaxLength)
        {
            return compact;
        }

        var cut = compact.LastIndexOfAny([' ', '\n'], MaxLength);
        var head = compact[..(cut > MaxLength / 2 ? cut : MaxLength)].TrimEnd(' ', '\n', ',', '.', ';', ':', '—', '-');
        return head + "…";
    }

    // A hashtag or mention: '#' / '@' at a word start followed by word characters (any script).
    [GeneratedRegex(@"(?<!\w)[#@][\w.]+", RegexOptions.CultureInvariant)]
    private static partial Regex Noise();

    [GeneratedRegex(@"[ \t\r]+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();
}
