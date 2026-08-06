using LeBot.Infrastructure.MediaExtraction.AutoRia;

namespace LeBot.Infrastructure.Tests.MediaExtraction.AutoRia;

public class AutoRiaListingParserTests
{
    // A richly-populated petrol SUV: every optional field (generation, drive type, colour) is present.
    private static AutoRiaListing ParseBmw() =>
        AutoRiaListingParser.Parse(
            LoadFixture("bmw_x5_39837585.html"),
            new Uri("https://auto.ria.com/uk/auto_bmw_x5_39837585.html"))!;

    // A sparse diesel bus: no generation and no drive type — exercises graceful omission.
    private static AutoRiaListing ParseSprinter() =>
        AutoRiaListingParser.Parse(
            LoadFixture("mercedes_sprinter_40089751.html"),
            new Uri("https://auto.ria.com/uk/auto_mercedes_benz_sprinter_40089751.html"))!;

    [Fact]
    public void Parse_Bmw_ReadsEveryHeadlineField()
    {
        var listing = ParseBmw();

        listing.Title.Should().Be("BMW X5 2019");
        listing.Generation.Should().Be("G05, 40i Steptronic (340 к.с.) xDrive");
        listing.Mileage.Should().Be("107 тис. км");
        listing.Gearbox.Should().Be("Автомат");
        listing.Engine.Should().Be("Бензин, 3 л, (340 к.с. / 250 кВт)");
        listing.DriveType.Should().Be("Повний");
        listing.Color.Should().Be("Білий");
        listing.BodyType.Should().Be("Позашляховик / Кросовер • 5 дверей • 5 місць");
        listing.Location.Should().Be("Київ");
    }

    [Fact]
    public void Parse_Bmw_FormatsBothCurrencies()
    {
        ParseBmw().Price.Should().Be("53 000 $ · 2 366 450 ₴");
    }

    [Fact]
    public void Parse_Bmw_KeepsSellerDescription()
    {
        ParseBmw().Description.Should().StartWith("Продається BMW X5 xDrive40i G05, 2019 року.");
    }

    [Fact]
    public void Parse_Bmw_TakesFirstTenGalleryPhotosStartingWithMainPhoto()
    {
        var listing = ParseBmw();

        listing.PhotoUrls.Should().HaveCount(10);
        listing.PhotoUrls[0].Should()
            .Be("https://cdn.riastatic.com/photosnew/auto/photo/bmw_x5__641500428hd.jpg");
        listing.PhotoUrls.Should().OnlyHaveUniqueItems();
        listing.PhotoUrls.Should().AllSatisfy(u => u.Should().EndWith("hd.jpg"));
    }

    [Fact]
    public void Parse_Bmw_ExcludesSimilarAdvertPhotos()
    {
        // 606457733 belongs to a "similar adverts" card, not this advert — it must never leak in.
        ParseBmw().PhotoUrls.Should().NotContain(u => u.Contains("606457733"));
    }

    [Fact]
    public void Parse_Sprinter_OmitsAbsentGenerationAndDriveType()
    {
        var listing = ParseSprinter();

        listing.Title.Should().Be("Mercedes-Benz Sprinter 2015");
        listing.Generation.Should().BeNull();
        listing.DriveType.Should().BeNull();
    }

    [Fact]
    public void Parse_Sprinter_ReadsDieselSpecsAndCity()
    {
        var listing = ParseSprinter();

        listing.Mileage.Should().Be("400 тис. км");
        listing.Gearbox.Should().Be("Автомат");
        listing.Engine.Should().Be("Дизель, 2.14 л");
        listing.Color.Should().Be("Синій");
        listing.Location.Should().Be("Кривий Ріг");
        listing.Price.Should().Be("29 999 $ · 1 339 455 ₴");
    }

    [Fact]
    public void Parse_Sprinter_ReadsHyphenatedPhotoBases()
    {
        var listing = ParseSprinter();

        listing.PhotoUrls.Should().HaveCount(10);
        listing.PhotoUrls[0].Should()
            .Be("https://cdn.riastatic.com/photosnew/auto/photo/mercedes-benz_sprinter__648425366hd.jpg");
    }

    [Fact]
    public void Parse_PageWithoutVehicleData_ReturnsNull()
    {
        var result = AutoRiaListingParser.Parse(
            "<html><head><title>Пошук</title></head><body>no advert here</body></html>",
            new Uri("https://auto.ria.com/uk/auto_ghost_00000000.html"));

        result.Should().BeNull();
    }

    private static string LoadFixture(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "_fixtures", "autoria", fileName));
}
