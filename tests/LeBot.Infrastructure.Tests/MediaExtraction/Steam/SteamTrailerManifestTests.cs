using System.Xml;
using LeBot.Infrastructure.MediaExtraction.Steam;

namespace LeBot.Infrastructure.Tests.MediaExtraction.Steam;

public class SteamTrailerManifestTests
{
    private const long Megabyte = 1024 * 1024;

    private static SteamTrailerManifest ParseFixture() => SteamTrailerManifest.Parse(SteamFixtures.TrailerManifest())!;

    [Fact]
    public void Parse_SteamTrailer_ReadsDurationAudioAndEveryH264Rendition()
    {
        var manifest = ParseFixture();

        manifest.Duration.Should().Be(TimeSpan.FromSeconds(81.8));
        manifest.AudioBandwidth.Should().Be(192_000);
        manifest.Renditions.Select(r => r.Height).Should().Equal(1080, 720, 480, 360);
    }

    [Fact]
    public void EstimateBytes_720p_CountsVideoAndAudioWithAMargin()
    {
        var manifest = ParseFixture();

        var estimate = manifest.EstimateBytes(manifest.Renditions.Single(r => r.Height == 720));

        // (2 600 000 + 192 000) bit/s ÷ 8 × 81.8 s × 1.1 — a real download of it measured 27.8 MB.
        estimate.Should().BeCloseTo(31_403_020, 2);
    }

    [Theory]
    [InlineData(49 * Megabyte, 720, 720)]
    [InlineData(100 * Megabyte, 1080, 1080)]
    [InlineData(100 * Megabyte, 720, 720)]
    [InlineData(20 * Megabyte, 720, 480)]
    [InlineData(13 * Megabyte, 720, 360)]
    public void PickRendition_TakesTheTallestThatFitsBothLimits(long budgetBytes, int maxHeight, int expectedHeight)
    {
        var manifest = ParseFixture();

        var rendition = manifest.PickRendition(budgetBytes, maxHeight);

        rendition!.Height.Should().Be(expectedHeight);
    }

    [Fact]
    public void PickRendition_EvenTheSmallestRunsOver_ReturnsNull()
    {
        var manifest = ParseFixture();

        var rendition = manifest.PickRendition(10 * Megabyte, 720);

        rendition.Should().BeNull();
    }

    [Fact]
    public void Parse_RenditionInAnotherCodec_IsLeftOut()
    {
        var mpd = Mpd(
            """
            <AdaptationSet contentType="video">
              <Representation codecs="av01.0.05M.08" bandwidth="2600000" height="720" />
              <Representation codecs="avc1.640029" bandwidth="1400000" height="480" />
            </AdaptationSet>
            """);

        var manifest = SteamTrailerManifest.Parse(mpd);

        manifest!.Renditions.Should().ContainSingle().Which.Height.Should().Be(480);
    }

    [Fact]
    public void Parse_RenditionNamingNoCodec_IsKept()
    {
        var mpd = Mpd(
            """
            <AdaptationSet contentType="video">
              <Representation bandwidth="1400000" height="480" />
            </AdaptationSet>
            """);

        var manifest = SteamTrailerManifest.Parse(mpd);

        manifest!.Renditions.Should().ContainSingle().Which.Height.Should().Be(480);
    }

    [Fact]
    public void Parse_StreamsTypedOnlyByMimeType_AreStillTold()
    {
        var mpd = Mpd(
            """
            <AdaptationSet>
              <Representation mimeType="video/mp4" codecs="avc1.640029" bandwidth="1400000" height="480" />
            </AdaptationSet>
            <AdaptationSet mimeType="audio/mp4">
              <Representation codecs="mp4a.40.2" bandwidth="128000" />
            </AdaptationSet>
            """);

        var manifest = SteamTrailerManifest.Parse(mpd);

        manifest!.Renditions.Should().ContainSingle();
        manifest.AudioBandwidth.Should().Be(128_000);
    }

    [Fact]
    public void Parse_TrailerWithoutAudio_BudgetsVideoAlone()
    {
        var mpd = Mpd(
            """
            <AdaptationSet contentType="video">
              <Representation codecs="avc1.640029" bandwidth="1400000" height="480" />
            </AdaptationSet>
            """);

        var manifest = SteamTrailerManifest.Parse(mpd);

        manifest!.AudioBandwidth.Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("PT0S")]
    [InlineData("eighty seconds")]
    public void Parse_NoUsableDuration_ReturnsNull(string duration)
    {
        var mpd = Mpd(
            """
            <AdaptationSet contentType="video">
              <Representation codecs="avc1.640029" bandwidth="1400000" height="480" />
            </AdaptationSet>
            """,
            duration);

        var manifest = SteamTrailerManifest.Parse(mpd);

        manifest.Should().BeNull();
    }

    [Fact]
    public void Parse_NoVideoRendition_ReturnsNull()
    {
        var mpd = Mpd(
            """
            <AdaptationSet contentType="audio">
              <Representation codecs="mp4a.40.2" bandwidth="192000" />
            </AdaptationSet>
            """);

        var manifest = SteamTrailerManifest.Parse(mpd);

        manifest.Should().BeNull();
    }

    [Fact]
    public void Parse_BodyIsNotXml_Throws()
    {
        var act = () => SteamTrailerManifest.Parse("{ \"error\": \"not found\" }");

        act.Should().Throw<XmlException>();
    }

    private static string Mpd(string adaptationSets, string duration = "PT1M21.8S") =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="{duration}">
          <Period id="0">
            {adaptationSets}
          </Period>
        </MPD>
        """;
}
