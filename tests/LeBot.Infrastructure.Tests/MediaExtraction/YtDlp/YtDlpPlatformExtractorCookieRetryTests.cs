using LeBot.Domain.Media;
using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.MediaExtraction.YtDlp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.Tests.MediaExtraction.YtDlp;

public class YtDlpPlatformExtractorCookieRetryTests
{
    private static readonly Uri PostUrl = new("https://www.instagram.com/reel/Cx1x2x3x4x5/");

    private static YtDlpPlatformExtractor CreateSut(string? cookiesFromBrowser)
    {
        var options = Options.Create(new YtDlpOptions
        {
            DownloadDirectory = Path.GetTempPath(),
            CookiesFromBrowser = cookiesFromBrowser,
        });
        return new YtDlpPlatformExtractor(options, NullLogger<YtDlpPlatformExtractor>.Instance);
    }

    [Fact]
    public void BuildOptionSet_CookiesConfigured_PassesThemToYtDlp()
    {
        var optionSet = CreateSut("firefox").BuildOptionSet();

        optionSet.CookiesFromBrowser.Should().Be("firefox");
    }

    [Fact]
    public void BuildOptionSet_CookiesSuppressed_OmitsThemEvenWhenConfigured()
    {
        // The anonymous second pass has to actually be anonymous — if the option leaked
        // through, the retry would repeat the dead session that caused the failure.
        var optionSet = CreateSut("firefox").BuildOptionSet(withBrowserCookies: false);

        optionSet.CookiesFromBrowser.Should().BeNull();
    }

    [Fact]
    public void ShouldRetryWithoutCookies_ContentUnavailable_ReturnsTrue()
    {
        // The Instagram shape: a revoked sessionid turns every reel into HTTP 400, and the
        // very same reel extracts fine with no cookies at all.
        var error = new ExtractionError.ContentUnavailable(
            PostUrl,
            "ERROR: [Instagram] Cx1x2x3x4x5: Video info extraction failed: HTTP Error 400: Bad Request");

        CreateSut("firefox").ShouldRetryWithoutCookies(error).Should().BeTrue();
    }

    [Fact]
    public void ShouldRetryWithoutCookies_ToolFailure_ReturnsTrue()
    {
        // Metadata can come back fine on a stale session and the media URL it hands out still
        // be signed for a session the platform has since dropped, so the download is the one
        // that fails. Worth the anonymous pass too.
        var error = new ExtractionError.ToolFailure("HTTP Error 403: Forbidden");

        CreateSut("firefox").ShouldRetryWithoutCookies(error).Should().BeTrue();
    }

    [Fact]
    public void ShouldRetryWithoutCookies_NoCookiesConfigured_ReturnsFalse()
    {
        // Nothing to drop, so the second pass would be byte-identical to the first.
        var error = new ExtractionError.ContentUnavailable(PostUrl, "HTTP Error 400: Bad Request");

        CreateSut(null).ShouldRetryWithoutCookies(error).Should().BeFalse();
    }

    [Fact]
    public void ShouldRetryWithoutCookies_UnsupportedPlatform_ReturnsFalse()
    {
        // Every shop and news link in the chat ends here. Re-running yt-dlp on all of them
        // would double the cost of the common case to rescue nothing.
        var error = new ExtractionError.UnsupportedPlatform(new Uri("https://rozetka.com.ua/"));

        CreateSut("firefox").ShouldRetryWithoutCookies(error).Should().BeFalse();
    }

    [Theory]
    [InlineData("ERROR: [TikTok] 123: Unable to extract universal data for rehydration")]
    [InlineData("ERROR: [TikTok] 123: Unexpected response from webpage request")]
    public void ShouldRetryWithoutCookies_TransientTikTokChallenge_ReturnsFalse(string detail)
    {
        // The challenge is not a session problem, and the transient pipeline has already
        // rolled it ten times — a cookieless encore would only make a slow failure slower.
        var error = new ExtractionError.ContentUnavailable(
            new Uri("https://vt.tiktok.com/ZSxxxxxxx"), detail);

        CreateSut("firefox").ShouldRetryWithoutCookies(error).Should().BeFalse();
    }
}
