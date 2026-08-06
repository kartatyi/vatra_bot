using System.Text;

namespace LeBot.Infrastructure.MediaExtraction.AutoRia;

/// <summary>
/// Formats an <see cref="AutoRiaListing"/> into the plain-text caption that rides along with the
/// photo album. Plain text (not Markdown/HTML) because the messenger sends captions without a parse
/// mode; structure comes from emoji and line breaks instead. Every spec line is optional and simply
/// skipped when its field is null, so a sparse advert still produces a tidy caption.
/// </summary>
internal static class AutoRiaCaptionBuilder
{
    public static string Build(AutoRiaListing listing)
    {
        var builder = new StringBuilder();

        builder.Append("🚗 ").Append(listing.Title?.Trim() ?? "AUTO.RIA");
        AppendLine(builder, listing.Generation, "▪️ ");

        var specs = new StringBuilder();
        AppendLine(specs, listing.Price, "💵 ");
        AppendLine(specs, listing.Mileage, "🛣 ");
        AppendLine(specs, listing.Gearbox, "⚙️ ");
        AppendLine(specs, listing.Engine, "⛽ ");
        AppendDrive(specs, listing.DriveType);
        AppendLine(specs, listing.Color, "🎨 ");
        AppendLine(specs, listing.BodyType, "🚘 ");
        AppendLine(specs, listing.Location, "📍 ");

        if (specs.Length > 0)
        {
            builder.Append("\n\n").Append(specs);
        }

        if (!string.IsNullOrWhiteSpace(listing.Description))
        {
            builder.Append("\n\n").Append(listing.Description.Trim());
        }

        return builder.ToString();
    }

    private static void AppendLine(StringBuilder builder, string? value, string prefix)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append('\n');
        }

        builder.Append(prefix).Append(value.Trim());
    }

    private static void AppendDrive(StringBuilder builder, string? driveType)
    {
        if (string.IsNullOrWhiteSpace(driveType))
        {
            return;
        }

        var trimmed = driveType.Trim();
        var text = trimmed.Contains("привід", StringComparison.OrdinalIgnoreCase) ? trimmed : $"{trimmed} привід";
        AppendLine(builder, text, "🚙 ");
    }
}
