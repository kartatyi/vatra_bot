using System.Text;

namespace LeBot.Infrastructure.MediaExtraction.Olx;

/// <summary>
/// Formats an <see cref="OlxListing"/> into the plain-text caption under the photo album. Plain text,
/// because the messenger sends captions without a parse mode. The order is the point: what a buyer
/// decides on — title, price, place — comes first, the parameters next, and the seller's description
/// last, so when Telegram's 1024-char caption limit cuts, it cuts the prose, not the price.
/// </summary>
internal static class OlxCaptionBuilder
{
    public static string Build(OlxListing listing)
    {
        var builder = new StringBuilder("🏷 ").Append(listing.Title);

        if (!listing.IsActive)
        {
            builder.Append("\n⛔ Оголошення вже неактивне");
        }

        if (listing.Price is { } price)
        {
            builder.Append("\n💵 ").Append(price);
            if (listing.Negotiable)
            {
                builder.Append(" · Договірна");
            }
        }

        if (listing.Location is { } location)
        {
            builder.Append("\n📍 ").Append(location);
        }

        if (listing.Parameters.Count > 0)
        {
            builder.Append('\n');
            foreach (var parameter in listing.Parameters)
            {
                builder.Append("\n• ").Append(parameter.Name).Append(": ").Append(parameter.Value);
            }
        }

        if (listing.Description is { } description)
        {
            builder.Append("\n\n").Append(description);
        }

        return builder.ToString();
    }
}
