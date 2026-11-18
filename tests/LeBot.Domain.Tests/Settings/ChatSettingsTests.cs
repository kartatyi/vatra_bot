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
        ChatSettings.Default.OfferFullText.Should().BeTrue();
        ChatSettings.Default.ShrugOnFailure.Should().BeFalse();
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

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc")]
    [InlineData("https://m.youtube.com/watch?v=abc")]
    [InlineData("https://music.youtube.com/watch?v=abc")]
    [InlineData("https://www.youtube.com/live/abc")]
    [InlineData("https://youtube.com/shorts/abc")]
    [InlineData("https://youtu.be/abc")]
    [InlineData("https://www.tiktok.com/@a/video/1")]
    [InlineData("https://vt.tiktok.com/ZSabc/")]
    [InlineData("https://vm.tiktok.com/ZMabc/")]
    public void ModeFor_NoMediaFromVideoFirstPlatform_UsesVideoCaption(string url)
    {
        var payload = Payload(url) with { Description = "the video's description" };

        Settings.ModeFor(payload).Should().Be(CaptionMode.Brief);
    }

    [Theory]
    [InlineData("https://www.threads.com/@a/post/1")]
    [InlineData("https://x.com/a/status/1")]
    [InlineData("https://www.reddit.com/r/a/comments/1")]
    [InlineData("https://www.instagram.com/p/abc")]
    [InlineData("https://notyoutube.com/watch?v=abc")]
    public void ModeFor_NoMediaFromAnyOtherHost_UsesPostCaption(string url)
    {
        var payload = Payload(url) with { Description = "the post" };

        Settings.ModeFor(payload).Should().Be(CaptionMode.Full);
    }

    [Fact]
    public void ModeFor_PhotoPostFromVideoFirstPlatform_UsesPostCaption()
    {
        var payload = Payload("https://www.tiktok.com/@a/photo/1", MediaKind.Photo, MediaKind.Photo);

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
    public void ShapeCaption_DefaultOnVideoFirstPostWithoutMedia_LeavesNothingToSend()
    {
        var payload = Payload("https://www.youtube.com/watch?v=abc") with
        {
            Title = "Full build walkthrough",
            Description = "00:00 intro\n01:12 sponsor https://example.com #build",
            Author = "channel",
        };

        var shaped = ChatSettings.Default.ShapeCaption(payload);

        shaped.Body.Should().BeNull();
        shaped.RevealableText.Should().BeNull();
    }

    [Fact]
    public void ShapeCaption_VideoFullOnVideoFirstPostWithoutMedia_KeepsTheText()
    {
        var payload = Payload("https://youtu.be/abc") with { Title = "Full build walkthrough", Description = "the description" };

        var shaped = new ChatSettings(CaptionMode.Full, CaptionMode.Full).ShapeCaption(payload);

        shaped.Should().BeSameAs(payload);
    }

    [Fact]
    public void ShapeCaption_DefaultOnTextFirstPostWithoutMedia_KeepsTheText()
    {
        var payload = Payload("https://www.threads.com/@a/post/1") with { Description = "the post" };

        var shaped = ChatSettings.Default.ShapeCaption(payload);

        shaped.Should().BeSameAs(payload);
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
