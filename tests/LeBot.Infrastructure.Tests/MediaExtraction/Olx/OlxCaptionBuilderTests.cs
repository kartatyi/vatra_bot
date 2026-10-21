using LeBot.Infrastructure.MediaExtraction.Olx;

namespace LeBot.Infrastructure.Tests.MediaExtraction.Olx;

public class OlxCaptionBuilderTests
{
    private static OlxListing FullListing() => new(
        Title: "SolarPlay Q2501",
        Price: "29 500 грн.",
        Negotiable: true,
        Location: "Київ, Дарницький",
        Parameters: [new OlxParameter("Стан", "Нове"), new OlxParameter("Ємність", "2160 Вт·год")],
        Description: "Нова в упаковці.\nСамовивіз.",
        IsActive: true,
        PhotoUrls: []);

    [Fact]
    public void Build_FullListing_LeadsWithTitlePriceAndPlace()
    {
        var lines = OlxCaptionBuilder.Build(FullListing()).Split('\n');

        lines[0].Should().Be("🏷 SolarPlay Q2501");
        lines[1].Should().Be("💵 29 500 грн. · Договірна");
        lines[2].Should().Be("📍 Київ, Дарницький");
    }

    [Fact]
    public void Build_FullListing_SetsParametersApartThenEndsWithDescription()
    {
        var caption = OlxCaptionBuilder.Build(FullListing());

        caption.Should().Contain("📍 Київ, Дарницький\n\n• Стан: Нове\n• Ємність: 2160 Вт·год\n\nНова в упаковці.");
        caption.Should().EndWith("Нова в упаковці.\nСамовивіз.");
    }

    [Fact]
    public void Build_FixedPrice_HasNoNegotiationMark()
    {
        var caption = OlxCaptionBuilder.Build(FullListing() with { Negotiable = false });

        caption.Should().Contain("💵 29 500 грн.\n");
    }

    [Fact]
    public void Build_InactiveListing_SaysSoRightUnderTheTitle()
    {
        var lines = OlxCaptionBuilder.Build(FullListing() with { IsActive = false }).Split('\n');

        lines[1].Should().Be("⛔ Оголошення вже неактивне");
    }

    [Fact]
    public void Build_TitleOnlyListing_IsJustTheTitle()
    {
        var listing = new OlxListing("Стіл", null, false, null, [], null, true, []);

        OlxCaptionBuilder.Build(listing).Should().Be("🏷 Стіл");
    }
}
