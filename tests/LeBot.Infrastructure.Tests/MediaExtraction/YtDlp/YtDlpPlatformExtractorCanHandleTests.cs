using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.MediaExtraction.YtDlp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.Tests.MediaExtraction.YtDlp;

public class YtDlpPlatformExtractorCanHandleTests
{
    private static YtDlpPlatformExtractor CreateSut()
    {
        var options = Options.Create(new YtDlpOptions
        {
            DownloadDirectory = Path.GetTempPath(),
        });
        return new YtDlpPlatformExtractor(options, NullLogger<YtDlpPlatformExtractor>.Instance);
    }

    [Theory]
    [InlineData("https://tiktok.com/@user/video/123")]
    [InlineData("https://www.tiktok.com/@user/video/123")]
    [InlineData("https://vm.tiktok.com/abc")]
    [InlineData("https://youtube.com/shorts/abc")]
    [InlineData("https://www.youtube.com/watch?v=abc")]
    [InlineData("https://youtu.be/abc")]
    [InlineData("https://instagram.com/reel/abc")]
    [InlineData("https://www.instagram.com/p/abc")]
    [InlineData("https://threads.net/@user/post/123")]
    [InlineData("https://threads.com/@user/post/123")]
    [InlineData("https://twitter.com/user/status/123")]
    [InlineData("https://x.com/user/status/123")]
    [InlineData("https://reddit.com/r/x/comments/y")]
    [InlineData("https://redd.it/abc")]
    [InlineData("https://vimeo.com/12345")]
    [InlineData("https://twitch.tv/user")]
    [InlineData("https://clips.twitch.tv/abc")]
    [InlineData("https://facebook.com/video")]
    [InlineData("https://fb.watch/x")]
    public void CanHandle_SupportedHosts_ReturnsTrue(string url)
    {
        CreateSut().CanHandle(new Uri(url)).Should().BeTrue();
    }

    [Theory]
    [InlineData("https://random.example.com/x")]
    [InlineData("https://news.bbc.co.uk/article")]
    [InlineData("https://github.com/user/repo")]
    [InlineData("https://wikipedia.org/foo")]
    public void CanHandle_UnsupportedHosts_ReturnsFalse(string url)
    {
        CreateSut().CanHandle(new Uri(url)).Should().BeFalse();
    }
}
