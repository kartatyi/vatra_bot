using LeBot.Application.Telemetry;

namespace LeBot.Application.Tests.Telemetry;

public class PlatformNameTests
{
    [Theory]
    [InlineData("tiktok.com", "TikTok")]
    [InlineData("vt.tiktok.com", "TikTok")]
    [InlineData("www.instagram.com", "Instagram")]
    [InlineData("youtu.be", "YouTube")]
    [InlineData("m.youtube.com", "YouTube")]
    [InlineData("threads.net", "Threads")]
    [InlineData("twitter.com", "X")]
    [InlineData("old.reddit.com", "Reddit")]
    [InlineData("store.steampowered.com", "Steam")]
    public void Of_KnownHost_NamesThePlatform(string host, string expected)
    {
        PlatformName.Of(host).Should().Be(expected);
    }

    [Theory]
    [InlineData("www.vimeo.com", "vimeo.com")]
    [InlineData("auto.ria.com", "auto.ria.com")]
    [InlineData("nottiktok.com", "nottiktok.com")]
    public void Of_UnknownHost_PassesThroughWithoutWww(string host, string expected)
    {
        PlatformName.Of(host).Should().Be(expected);
    }
}
