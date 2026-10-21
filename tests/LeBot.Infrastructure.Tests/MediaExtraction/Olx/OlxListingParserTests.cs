using System.Text.Json;
using LeBot.Infrastructure.MediaExtraction.Olx;

namespace LeBot.Infrastructure.Tests.MediaExtraction.Olx;

public class OlxListingParserTests
{
    // A business seller's new item: two photos, one parameter, a <br />-separated description.
    private static OlxListing ParseSolarPlay() => Parse("solarplay_q2501_ID11svhc.html");

    // A car: fourteen photos and twenty parameters, five of them long option lists or prose.
    private static OlxListing ParseAudi() => Parse("audi_a4_ID11nfyE.html");

    // A private seller open to haggling, with blank lines between paragraphs.
    private static OlxListing ParseGarmin() => Parse("garmin_vivoactive_IDXjUJw.html");

    // A giveaway: no regular price, and a city without a district.
    private static OlxListing ParseFreeFurniture() => Parse("free_furniture_ID11lAGo.html");

    [Fact]
    public void Parse_SolarPlay_ReadsTitlePriceAndPlace()
    {
        var listing = ParseSolarPlay();

        listing.Title.Should().Be("SolarPlay Q2501 2400W (2160Wh) LiFePO4");
        listing.Price.Should().Be("29 500 грн.");
        listing.Negotiable.Should().BeFalse();
        listing.Location.Should().Be("Київ, Дарницький");
        listing.IsActive.Should().BeTrue();
        listing.Parameters.Should().Equal(new OlxParameter("Стан", "Нове"));
    }

    [Fact]
    public void Parse_SolarPlay_TurnsBreakTagsIntoLines()
    {
        var description = ParseSolarPlay().Description;

        description.Should().StartWith("SolarPlay Q2501 2400W (2160Wh) LiFePO4 — Нова в упаковці\nПотужна та надійна");
        description.Should().NotContain("<br");
    }

    [Fact]
    public void Parse_SolarPlay_KeepsGalleryPhotosInOrder()
    {
        ParseSolarPlay().PhotoUrls.Should().Equal(
            "https://ireland.apollo.olxcdn.com:443/v1/files/dob40hakzfk5-UA/image;s=864x1000",
            "https://ireland.apollo.olxcdn.com:443/v1/files/lyfzglp42lh91-UA/image;s=913x1000");
    }

    [Fact]
    public void Parse_Garmin_MarksTheNegotiablePrice()
    {
        var listing = ParseGarmin();

        listing.Price.Should().Be("1 500 грн.");
        listing.Negotiable.Should().BeTrue();
    }

    [Fact]
    public void Parse_Garmin_KeepsOneBlankLineBetweenParagraphs()
    {
        ParseGarmin().Description.Should()
            .Contain("але не критично\n\nВ комплекті: годинник, зарядка.\n\nВерсія ПО");
    }

    [Fact]
    public void Parse_Audi_CapsTheGalleryAtAnAlbum()
    {
        var photos = ParseAudi().PhotoUrls;

        photos.Should().HaveCount(10);
        photos.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Parse_Audi_KeepsShortParametersInPageOrder()
    {
        var parameters = ParseAudi().Parameters;

        parameters[0].Should().Be(new OlxParameter("Тип автомобіля", "З пробігом"));
        parameters.Should().Contain(new OlxParameter("Пробіг", "387 тис.км."));
        parameters.Should().Contain(new OlxParameter("Технічний стан", "На ходу, технічно справна"));
        parameters.Select(p => p.Name).Should().NotContain(["Комфорт", "Мультимедіа", "Безпека", "Лакофарбове покриття"]);
    }

    [Fact]
    public void Parse_Audi_ShowsThePriceInTheSellersCurrency()
    {
        ParseAudi().Price.Should().Be("3 999 $");
    }

    [Fact]
    public void Parse_FreeFurniture_ReadsTheGiveawayAndTheBareCity()
    {
        var listing = ParseFreeFurniture();

        listing.Price.Should().Be("Безкоштовно");
        listing.Negotiable.Should().BeFalse();
        listing.Location.Should().Be("Тернопіль");
    }

    [Fact]
    public void Parse_ClosedAdvert_ReportsItInactive()
    {
        var html = PageWithState(new { ad = new { ad = new { title = "Велосипед", isActive = false } } });

        OlxListingParser.Parse(html)!.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Parse_ParagraphHtml_BecomesPlainParagraphs()
    {
        var html = PageWithState(new
        {
            ad = new { ad = new { title = "Стіл", description = "<p>Дубовий&nbsp;стіл</p><p>Самовивіз &amp; торг</p>" } },
        });

        OlxListingParser.Parse(html)!.Description.Should().Be("Дубовий стіл\n\nСамовивіз & торг");
    }

    [Fact]
    public void Parse_SparseAdvert_LeavesTheMissingFieldsEmpty()
    {
        var html = PageWithState(new { ad = new { ad = new { title = "Стіл" } } });

        var listing = OlxListingParser.Parse(html)!;

        listing.Price.Should().BeNull();
        listing.Location.Should().BeNull();
        listing.Description.Should().BeNull();
        listing.Parameters.Should().BeEmpty();
        listing.PhotoUrls.Should().BeEmpty();
    }

    [Fact]
    public void Parse_JobAdvertState_ReturnsNull()
    {
        var html = PageWithState(new { jobAd = new { job = new { title = "Комплектувальник" } } });

        OlxListingParser.Parse(html).Should().BeNull();
    }

    [Theory]
    [InlineData("<html><body>Оголошення не знайдено</body></html>")]
    [InlineData("<script>window.__PRERENDERED_STATE__= \"not json\";</script>")]
    [InlineData("<script>window.__PRERENDERED_STATE__= \"{\\\"ad\\\":")]
    public void Parse_PageWithoutReadableState_ReturnsNull(string html)
    {
        OlxListingParser.Parse(html).Should().BeNull();
    }

    private static OlxListing Parse(string fixture) =>
        OlxListingParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "_fixtures", "olx", fixture)))!;

    /// <summary>A page carrying <paramref name="state"/> the way OLX ships it: JSON inside a JS string literal.</summary>
    private static string PageWithState(object state) =>
        $"<script>\n        window.__PRERENDERED_STATE__= {JsonSerializer.Serialize(JsonSerializer.Serialize(state))};\n</script>";
}
