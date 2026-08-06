namespace LeBot.Infrastructure.MediaExtraction.AutoRia;

/// <summary>
/// The fields pulled out of an auto.ria advert page: the human-readable spec lines plus the
/// gallery photo URLs. Every field except <see cref="Title"/> and <see cref="PhotoUrls"/> is
/// optional — auto.ria omits generation, drive type, colour, etc. on plenty of listings, so the
/// caption builder simply skips whatever is null.
/// </summary>
/// <param name="Title">Make, model and year, e.g. "BMW X5 2019".</param>
/// <param name="Generation">Generation / trim / modification line, e.g. "G05, 40i Steptronic (340 к.с.) xDrive".</param>
/// <param name="Mileage">Odometer reading already formatted for display, e.g. "107 тис. км".</param>
/// <param name="Gearbox">Transmission, e.g. "Автомат".</param>
/// <param name="Engine">Engine / fuel line, e.g. "Бензин, 3 л, (340 к.с. / 250 кВт)".</param>
/// <param name="DriveType">Drive, e.g. "Повний".</param>
/// <param name="Color">Body colour, e.g. "Білий".</param>
/// <param name="BodyType">Body / characteristics line, e.g. "Позашляховик / Кросовер • 5 дверей • 5 місць".</param>
/// <param name="Location">Seller city, e.g. "Київ".</param>
/// <param name="Price">Price line already formatted, e.g. "53 000 $ · 2 366 450 ₴".</param>
/// <param name="Description">The seller's free-text description.</param>
/// <param name="PhotoUrls">Gallery photo URLs (advert's own, recommendations excluded), capped upstream.</param>
internal sealed record AutoRiaListing(
    string? Title,
    string? Generation,
    string? Mileage,
    string? Gearbox,
    string? Engine,
    string? DriveType,
    string? Color,
    string? BodyType,
    string? Location,
    string? Price,
    string? Description,
    IReadOnlyList<string> PhotoUrls);
