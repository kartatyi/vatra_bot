using System.Globalization;
using System.Numerics;

namespace LeBot.Infrastructure.MediaExtraction.Instagram;

/// <summary>
/// Converts an Instagram shortcode (the <c>DaGKRNaCLiX</c> in <c>/p/DaGKRNaCLiX/</c>) into the
/// numeric media id its private web API is keyed by. The shortcode is a base64 (URL alphabet)
/// encoding of the media's big-integer primary key, so decoding is a straight positional base-64
/// expansion — no network round-trip needed to learn the id.
/// </summary>
internal static class InstagramMediaId
{
    private const string Alphabet =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

    /// <summary>
    /// Decodes <paramref name="shortcode"/> to its media id, or returns <c>null</c> when it carries
    /// a character outside the Instagram base64 alphabet (i.e. it isn't a real shortcode).
    /// </summary>
    public static string? FromShortcode(string shortcode)
    {
        if (string.IsNullOrEmpty(shortcode))
        {
            return null;
        }

        var id = BigInteger.Zero;
        foreach (var c in shortcode)
        {
            var digit = Alphabet.IndexOf(c);
            if (digit < 0)
            {
                return null;
            }

            id = (id * 64) + digit;
        }

        return id.ToString(CultureInfo.InvariantCulture);
    }
}
