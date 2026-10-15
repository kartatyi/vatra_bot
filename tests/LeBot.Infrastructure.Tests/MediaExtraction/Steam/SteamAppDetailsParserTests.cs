using System.Text.Json;
using LeBot.Infrastructure.MediaExtraction.Steam;

namespace LeBot.Infrastructure.Tests.MediaExtraction.Steam;

public class SteamAppDetailsParserTests
{
    private static SteamApp ParseDiscounted() =>
        SteamAppDetailsParser.Parse(SteamFixtures.AppDetails(SteamFixtures.DiscountedAppId), SteamFixtures.DiscountedAppId)!;

    private static SteamApp ParseComingSoon() =>
        SteamAppDetailsParser.Parse(SteamFixtures.AppDetails(SteamFixtures.ComingSoonAppId), SteamFixtures.ComingSoonAppId)!;

    [Fact]
    public void Parse_ReleasedApp_ReadsTheHeadlineFacts()
    {
        var app = ParseDiscounted();

        app.Name.Should().Be("RV There Yet?");
        app.ShortDescription.Should().Be("A co-op adventure about driving your Recreational Vehicle home.");
        app.Developer.Should().Be("Nuggets Entertainment");
        app.ReleaseDate.Should().Be("21 жовт. 2025");
        app.ComingSoon.Should().BeFalse();
        app.Genres.Should().Equal("Бойовики", "Пригоди", "Інді");
    }

    [Fact]
    public void Parse_DiscountedApp_KeepsBothPricesAndThePercent()
    {
        var app = ParseDiscounted();

        app.Price.Should().Be(new SteamPrice(Final: "112₴", Initial: "187₴", DiscountPercent: 40));
    }

    [Fact]
    public void Parse_ReleasedApp_ListsFullSizeScreenshotsInStoreOrder()
    {
        var app = ParseDiscounted();

        app.ScreenshotUrls.Should().HaveCount(10);
        app.ScreenshotUrls[0].AbsoluteUri.Should().Contain("ss_ed2f7080c6622688a72a86ca85af600f2a2bdfb9.1920x1080.jpg");
    }

    [Fact]
    public void Parse_SeveralHighlightedTrailers_TakesTheH264ManifestOfTheFirst()
    {
        var app = ParseDiscounted();

        app.TrailerManifestUrl!.AbsoluteUri.Should().Be(
            "https://video.akamai.steamstatic.com/store_trailers/3949040/1309387426/dce3fd504257a257abfe259fa3207a0bcf499582/1789049802/dash_h264.mpd?t=1789134288");
    }

    [Fact]
    public void Parse_UnreleasedApp_HasNoPriceAndIsComingSoon()
    {
        var app = ParseComingSoon();

        app.Price.Should().BeNull();
        app.ComingSoon.Should().BeTrue();
        app.ReleaseDate.Should().Be("Незабаром");
    }

    [Fact]
    public void Parse_HtmlEscapedDescription_IsDecoded()
    {
        var app = ParseComingSoon();

        app.ShortDescription.Should().StartWith("\"Roguelike Card Game: Business Empire\" combines");
    }

    [Fact]
    public void Parse_TrailerNotFlaggedHighlight_LosesToALaterHighlightedOne()
    {
        var json = Details(
            """
            "movies": [
              { "highlight": false, "dash_h264": "https://video.test/extra/dash_h264.mpd" },
              { "highlight": true, "dash_h264": "https://video.test/lead/dash_h264.mpd" }
            ]
            """);

        var app = SteamAppDetailsParser.Parse(json, 42);

        app!.TrailerManifestUrl.Should().Be(new Uri("https://video.test/lead/dash_h264.mpd"));
    }

    [Fact]
    public void Parse_DescriptionWithMarkup_KeepsOnlyTheText()
    {
        var json = Details(""" "short_description": "<b>Bold</b>&nbsp;claim &lt;3\n  and more" """);

        var app = SteamAppDetailsParser.Parse(json, 42);

        app!.ShortDescription.Should().Be("Bold claim <3 and more");
    }

    [Fact]
    public void Parse_StaleInitialPriceWithoutDiscount_IsNotReportedAsADiscount()
    {
        var json = Details(
            """
            "price_overview": { "final_formatted": "225₴", "initial_formatted": "300₴", "discount_percent": 0 }
            """);

        var app = SteamAppDetailsParser.Parse(json, 42);

        app!.Price.Should().Be(new SteamPrice(Final: "225₴", Initial: null, DiscountPercent: 0));
    }

    [Fact]
    public void Parse_AppWithoutMediaFields_StillParsesWithEmptyLists()
    {
        var json = Details(string.Empty);

        var app = SteamAppDetailsParser.Parse(json, 42);

        app.Should().NotBeNull();
        app!.ScreenshotUrls.Should().BeEmpty();
        app.Genres.Should().BeEmpty();
        app.TrailerManifestUrl.Should().BeNull();
        app.HeaderImageUrl.Should().BeNull();
        app.Developer.Should().BeNull();
    }

    [Theory]
    [InlineData("""{ "42": { "success": false } }""")]
    [InlineData("""{ "7": { "success": true, "data": { "name": "Someone else" } } }""")]
    [InlineData("""{ "42": { "success": true, "data": { "name": "   " } } }""")]
    [InlineData("""{ "42": { "success": true } }""")]
    [InlineData("null")]
    [InlineData("[]")]
    public void Parse_StoreHoldsNoPageForTheApp_ReturnsNull(string json)
    {
        var app = SteamAppDetailsParser.Parse(json, 42);

        app.Should().BeNull();
    }

    [Fact]
    public void Parse_BodyIsNotJson_Throws()
    {
        var act = () => SteamAppDetailsParser.Parse("<html>rate limited</html>", 42);

        act.Should().Throw<JsonException>();
    }

    private static string Details(string extraFields)
    {
        var separator = string.IsNullOrWhiteSpace(extraFields) ? string.Empty : ",";
        return $$"""{ "42": { "success": true, "data": { "name": "Test App"{{separator}} {{extraFields}} } } }""";
    }
}
