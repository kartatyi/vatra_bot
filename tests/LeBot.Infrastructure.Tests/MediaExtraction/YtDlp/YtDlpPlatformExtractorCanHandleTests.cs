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
    [InlineData("https://vt.tiktok.com/ZSxbGhG4R/")]
    [InlineData("https://m.tiktok.com/v/123")]
    [InlineData("https://youtu.be/abc")]
    [InlineData("https://instagram.com/reel/abc")]
    [InlineData("https://threads.com/@user/post/123")]
    [InlineData("https://x.com/user/status/123")]
    [InlineData("https://vimeo.com/12345")]
    [InlineData("https://random.example.com/x")]
    [InlineData("https://github.com/user/repo")]
    [InlineData("http://insecure.example.com/article")]
    public void CanHandle_HttpAndHttpsUrls_ReturnsTrue(string url)
    {
        // The extractor claims every http(s) URL and lets yt-dlp's own extractor matrix decide
        // at ExtractAsync time. Unrecognised hosts come back as ExtractionError.UnsupportedPlatform
        // and the handler skips them silently — see HandleIncomingMessageHandler tests.
        CreateSut().CanHandle(new Uri(url)).Should().BeTrue();
    }

    [Theory]
    [InlineData("ftp://example.com/file")]
    [InlineData("file:///etc/passwd")]
    [InlineData("mailto:user@example.com")]
    public void CanHandle_NonHttpSchemes_ReturnsFalse(string url)
    {
        CreateSut().CanHandle(new Uri(url, UriKind.RelativeOrAbsolute)).Should().BeFalse();
    }
}
