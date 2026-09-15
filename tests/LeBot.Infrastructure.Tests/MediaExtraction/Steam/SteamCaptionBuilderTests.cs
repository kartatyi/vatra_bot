using LeBot.Infrastructure.MediaExtraction.Steam;

namespace LeBot.Infrastructure.Tests.MediaExtraction.Steam;

public class SteamCaptionBuilderTests
{
    [Fact]
    public void Build_EveryFactPresent_StacksThemBetweenNameAndDescription()
    {
        var app = App() with
        {
            Price = new SteamPrice(Final: "112₴", Initial: "187₴", DiscountPercent: 40),
            ReleaseDate = "21 жовт. 2025",
            Genres = ["Бойовики", "Пригоди", "Інді"],
        };
        var reviews = new SteamReviewSummary("дуже схвальні", TotalPositive: 77039, TotalReviews: 85435);

        var caption = SteamCaptionBuilder.Build(app, reviews);

        caption.Should().Be(
            "🎮 RV There Yet?\n\n" +
            "💵 187₴ → 112₴ (−40%)\n" +
            "📅 21 жовт. 2025\n" +
            "🏷 Бойовики · Пригоди · Інді\n" +
            "⭐ дуже схвальні — 90% · 85 435\n\n" +
            "A co-op adventure about driving your Recreational Vehicle home.");
    }

    [Fact]
    public void Build_UndiscountedPrice_ShowsOnePrice()
    {
        var app = App() with { Price = new SteamPrice(Final: "225₴", Initial: null, DiscountPercent: 0) };

        var caption = SteamCaptionBuilder.Build(app, reviews: null);

        caption.Should().Contain("💵 225₴\n").And.NotContain("→");
    }

    [Fact]
    public void Build_UnreleasedApp_MarksTheDateAsUpcoming()
    {
        var app = App() with { ReleaseDate = "Незабаром", ComingSoon = true };

        var caption = SteamCaptionBuilder.Build(app, reviews: null);

        caption.Should().Contain("⏳ Незабаром").And.NotContain("📅");
    }

    [Fact]
    public void Build_OnlyTheName_IsJustTheNameLine()
    {
        var app = App() with { ShortDescription = null };

        var caption = SteamCaptionBuilder.Build(app, reviews: null);

        caption.Should().Be("🎮 RV There Yet?");
    }

    [Fact]
    public void Build_NoFactsButADescription_SkipsStraightToIt()
    {
        var caption = SteamCaptionBuilder.Build(App(), reviews: null);

        caption.Should().Be("🎮 RV There Yet?\n\nA co-op adventure about driving your Recreational Vehicle home.");
    }

    private static SteamApp App() => new(
        Name: "RV There Yet?",
        ShortDescription: "A co-op adventure about driving your Recreational Vehicle home.",
        Developer: "Nuggets Entertainment",
        Price: null,
        ReleaseDate: null,
        ComingSoon: false,
        Genres: [],
        ScreenshotUrls: [],
        HeaderImageUrl: null,
        TrailerManifestUrl: null);
}
