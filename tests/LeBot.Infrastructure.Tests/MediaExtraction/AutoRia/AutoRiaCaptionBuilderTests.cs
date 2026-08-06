using LeBot.Infrastructure.MediaExtraction.AutoRia;

namespace LeBot.Infrastructure.Tests.MediaExtraction.AutoRia;

public class AutoRiaCaptionBuilderTests
{
    private static AutoRiaListing FullListing() => new(
        Title: "BMW X5 2019",
        Generation: "G05, 40i Steptronic (340 к.с.) xDrive",
        Mileage: "107 тис. км",
        Gearbox: "Автомат",
        Engine: "Бензин, 3 л",
        DriveType: "Повний",
        Color: "Білий",
        BodyType: "Позашляховик / Кросовер",
        Location: "Київ",
        Price: "53 000 $ · 2 366 450 ₴",
        Description: "Гарний стан.",
        PhotoUrls: []);

    [Fact]
    public void Build_FullListing_StartsWithTitleThenGeneration()
    {
        var caption = AutoRiaCaptionBuilder.Build(FullListing());

        var lines = caption.Split('\n');
        lines[0].Should().Be("🚗 BMW X5 2019");
        lines[1].Should().Be("▪️ G05, 40i Steptronic (340 к.с.) xDrive");
    }

    [Fact]
    public void Build_FullListing_IncludesEverySpecAndEndsWithDescription()
    {
        var caption = AutoRiaCaptionBuilder.Build(FullListing());

        caption.Should().ContainAll(
            "🛣 107 тис. км",
            "⚙️ Автомат",
            "⛽ Бензин, 3 л",
            "🎨 Білий",
            "📍 Київ",
            "💵 53 000 $ · 2 366 450 ₴");
        caption.Should().EndWith("Гарний стан.");
    }

    [Fact]
    public void Build_DriveType_AppendsPrividWord()
    {
        AutoRiaCaptionBuilder.Build(FullListing()).Should().Contain("🚙 Повний привід");
    }

    [Fact]
    public void Build_DriveTypeAlreadyContainingPrivid_IsNotDoubled()
    {
        var listing = FullListing() with { DriveType = "Повний привід" };

        AutoRiaCaptionBuilder.Build(listing).Should().Contain("🚙 Повний привід")
            .And.NotContain("привід привід");
    }

    [Fact]
    public void Build_SparseListing_SkipsMissingFields()
    {
        var listing = new AutoRiaListing(
            Title: "Mercedes-Benz Sprinter 2015",
            Generation: null,
            Mileage: "400 тис. км",
            Gearbox: "Автомат",
            Engine: "Дизель, 2.14 л",
            DriveType: null,
            Color: null,
            BodyType: null,
            Location: "Кривий Ріг",
            Price: null,
            Description: null,
            PhotoUrls: []);

        var caption = AutoRiaCaptionBuilder.Build(listing);

        caption.Should().StartWith("🚗 Mercedes-Benz Sprinter 2015");
        caption.Should().NotContain("▪️"); // no generation line
        caption.Should().NotContain("🚙"); // no drive line
        caption.Should().NotContain("🎨"); // no colour line
        caption.Should().NotContain("💵"); // no price line
        caption.Should().ContainAll("🛣 400 тис. км", "⚙️ Автомат", "📍 Кривий Ріг");
    }

    [Fact]
    public void Build_NullTitle_FallsBackToBrandName()
    {
        var listing = FullListing() with { Title = null };

        AutoRiaCaptionBuilder.Build(listing).Should().StartWith("🚗 AUTO.RIA");
    }
}
