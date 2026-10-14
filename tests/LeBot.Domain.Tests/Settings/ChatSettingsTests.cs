using LeBot.Domain.Media;
using LeBot.Domain.Settings;

namespace LeBot.Domain.Tests.Settings;

public class ChatSettingsTests
{
    private static readonly ChatSettings Settings = new(CaptionMode.Brief, CaptionMode.Full);

    [Fact]
    public void Default_DropsVideoTextAndKeepsPostText()
    {
        ChatSettings.Default.Should().Be(new ChatSettings(CaptionMode.Off, CaptionMode.Full));
    }

    [Theory]
    [InlineData("https://www.tiktok.com/@a/video/1")]
    [InlineData("https://www.instagram.com/reel/abc")]
    [InlineData("https://youtube.com/shorts/xyz")]
    public void ModeFor_VideoFromVideoPlatform_UsesVideoCaption(string url)
    {
        var payload = Payload(url, MediaKind.Video);

        Settings.ModeFor(payload).Should().Be(CaptionMode.Brief);
    }

    [Theory]
    [InlineData("https://www.threads.com/@a/post/1")]
    [InlineData("https://www.threads.net/@a/post/1")]
    [InlineData("https://x.com/a/status/1")]
    [InlineData("https://mobile.twitter.com/a/status/1")]
    [InlineData("https://old.reddit.com/r/a/comments/1")]
    public void ModeFor_VideoFromTextFirstPlatform_UsesPostCaption(string url)
    {
        var payload = Payload(url, MediaKind.Video);

        Settings.ModeFor(payload).Should().Be(CaptionMode.Full);
    }

    [Fact]
    public void ModeFor_PhotoPost_UsesPostCaption()
    {
        var payload = Payload("https://www.instagram.com/p/abc", MediaKind.Photo, MediaKind.Photo);

        Settings.ModeFor(payload).Should().Be(CaptionMode.Full);
    }

    [Fact]
    public void ModeFor_TextOnlyPayload_UsesPostCaption()
    {
        var payload = Payload("https://www.tiktok.com/@a/video/1");

        Settings.ModeFor(payload).Should().Be(CaptionMode.Full);
    }

    [Fact]
    public void ModeFor_LookalikeHost_IsNotTreatedAsTextFirst()
    {
        var payload = Payload("https://notx.com/clip", MediaKind.Video);

        Settings.ModeFor(payload).Should().Be(CaptionMode.Brief);
    }

    [Fact]
    public void Constructor_PostCaptionOff_Throws()
    {
        var create = () => new ChatSettings(CaptionMode.Full, CaptionMode.Off);

        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ShapeCaption_Off_DropsEveryCaptionSource_KeepsMedia()
    {
        var payload = Payload("https://www.tiktok.com/@a/video/1", MediaKind.Video) with
        {
            Title = "Video by a",
            Description = "promo #tag",
            Author = "a",
            RetainFiles = true,
        };

        var shaped = new ChatSettings(CaptionMode.Off, CaptionMode.Full).ShapeCaption(payload);

        shaped.Should().Be(payload with
        {
            Title = null,
            Description = null,
            Author = null,
            RevealableText = payload.Body,
        });
    }

    [Fact]
    public void ShapeCaption_OfferFullTextOff_HidesWithoutKeepingTheText()
    {
        var payload = Payload("https://www.tiktok.com/@a/video/1", MediaKind.Video) with { Description = "promo" };

        var shaped = new ChatSettings(CaptionMode.Off, CaptionMode.Full, OfferFullText: false).ShapeCaption(payload);

        shaped.RevealableText.Should().BeNull();
    }

    [Fact]
    public void ShapeCaption_BriefThatChangedNothing_OffersNoButton()
    {
        var payload = Payload("https://www.tiktok.com/@a/video/1", MediaKind.Video) with { Description = "short and clean" };

        Settings.ShapeCaption(payload).RevealableText.Should().BeNull();
    }

    [Fact]
    public void ShapeCaption_NoTextAtAll_OffersNoButton()
    {
        var payload = Payload("https://www.tiktok.com/@a/video/1", MediaKind.Video);

        ChatSettings.Default.ShapeCaption(payload).RevealableText.Should().BeNull();
    }

    [Fact]
    public void ShapeCaption_Short_ShortensDescription_KeepsAuthor()
    {
        var payload = Payload("https://www.tiktok.com/@a/video/1", MediaKind.Video) with
        {
            Title = "Video by a",
            Description = "Nice clip #fyp",
            Author = "a",
        };

        var shaped = Settings.ShapeCaption(payload);

        shaped.Should().Be(payload with { Title = null, Description = "Nice clip", RevealableText = payload.Body });
    }

    [Fact]
    public void ShapeCaption_ShortWithoutDescription_FallsBackToTitle()
    {
        var payload = Payload("https://youtube.com/shorts/x", MediaKind.Video) with { Title = "How to fold a shirt" };

        Settings.ShapeCaption(payload).Description.Should().Be("How to fold a shirt");
    }

    [Fact]
    public void ShapeCaption_Full_ReturnsThePayloadUnchanged()
    {
        var payload = Payload("https://www.threads.com/@a/post/1", MediaKind.Video) with { Description = "the post" };

        Settings.ShapeCaption(payload).Should().BeSameAs(payload);
    }

    private static MediaPayload Payload(string url, params MediaKind[] kinds) =>
        new(new Uri(url), Title: null, Author: null,
            Items: kinds.Select((kind, i) => new MediaItem($"{i}.bin", kind, null, null, null)).ToList());
}
