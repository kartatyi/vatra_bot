using LeBot.Domain.Common;
using LeBot.Domain.Media;
using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.MediaExtraction.YtDlp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.Tests.MediaExtraction.YtDlp;

public class YtDlpPlatformExtractorCookieRetryTests
{
    private static readonly Uri ReelUrl = new("https://www.instagram.com/reel/Cx1x2x3x4x5/");

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
    public void BuildOptionSet_AnonymousPass_OmitsCookiesEvenWhenConfigured()
    {
        // The first pass has to actually be anonymous — if the option leaked through, every link
        // would spend the account, which is the exact traffic that gets one banned.
        var optionSet = CreateSut("firefox").BuildOptionSet(withBrowserCookies: false);

        optionSet.CookiesFromBrowser.Should().BeNull();
    }

    [Fact]
    public void BuildOptionSet_CookiedPass_PassesThemToYtDlp()
    {
        var optionSet = CreateSut("firefox").BuildOptionSet(withBrowserCookies: true);

        optionSet.CookiesFromBrowser.Should().Be("firefox");
    }

    [Fact]
    public void ShouldRetryWithCookies_LoginGatedContent_ReturnsTrue()
    {
        // The case the session is kept for: a post the platform shows only to a logged-in viewer.
        var error = new ExtractionError.ContentUnavailable(
            new Uri("https://vt.tiktok.com/ZS4gXdkqH"),
            "ERROR: [TikTok] 123: This post may not be comfortable for some audiences. Log in for access.");

        CreateSut("firefox").ShouldRetryWithCookies(error.Url, error).Should().BeTrue();
    }

    [Fact]
    public void ShouldRetryWithCookies_ToolFailure_ReturnsTrue()
    {
        // Metadata can come back fine anonymously and the download still be refused without a
        // session. Worth the cookied pass too.
        var error = new ExtractionError.ToolFailure("HTTP Error 403: Forbidden");

        CreateSut("firefox").ShouldRetryWithCookies(ReelUrl, error).Should().BeTrue();
    }

    [Fact]
    public void ShouldRetryWithCookies_NoCookiesConfigured_ReturnsFalse()
    {
        // Nothing to add, so the second pass would be byte-identical to the first.
        var error = new ExtractionError.ContentUnavailable(ReelUrl, "HTTP Error 400: Bad Request");

        CreateSut(null).ShouldRetryWithCookies(ReelUrl, error).Should().BeFalse();
    }

    [Fact]
    public void ShouldRetryWithCookies_UnsupportedPlatform_ReturnsFalse()
    {
        // Every shop and news link in the chat ends here. Re-running yt-dlp on all of them
        // would double the cost of the common case to rescue nothing.
        var url = new Uri("https://rozetka.com.ua/");
        var error = new ExtractionError.UnsupportedPlatform(url);

        CreateSut("firefox").ShouldRetryWithCookies(url, error).Should().BeFalse();
    }

    [Theory]
    [InlineData("ERROR: [TikTok] 123: Unable to extract universal data for rehydration")]
    [InlineData("ERROR: [TikTok] 123: Unexpected response from webpage request")]
    public void ShouldRetryWithCookies_TransientTikTokChallenge_ReturnsFalse(string detail)
    {
        // The challenge is not a login problem, and the transient pipeline has already rolled
        // it ten times — a cookied encore would only make a slow failure slower.
        var error = new ExtractionError.ContentUnavailable(
            new Uri("https://vt.tiktok.com/ZSxxxxxxx"), detail);

        CreateSut("firefox").ShouldRetryWithCookies(error.Url, error).Should().BeFalse();
    }

    [Theory]
    [InlineData("https://www.instagram.com/p/DaGKRNaCLiX/")]
    [InlineData("https://www.instagram.com/tv/DaGKRNaCLiX/")]
    public void ShouldRetryWithCookies_InstagramPostTheApiExtractorServes_ReturnsFalse(string link)
    {
        // InstagramApiExtractor runs after yt-dlp with the session; a cookied yt-dlp pass first
        // would spend the account twice on one link.
        var url = new Uri(link);
        var error = new ExtractionError.ContentUnavailable(url, "HTTP Error 401: Unauthorized");

        CreateSut("firefox").ShouldRetryWithCookies(url, error).Should().BeFalse();
    }

    [Fact]
    public void KeepAnonymousFailureOverEmptyRetry_CookiedPassFoundOnlyText_ReturnsAnonymousFailure()
    {
        var anonymous = new Result<MediaPayload, ExtractionError>.Err(new ExtractionError.ToolFailure("HTTP Error 403: Forbidden"));
        var cookied = Result<MediaPayload, ExtractionError>.Success(new MediaPayload(ReelUrl, "title", null, [], null));

        var result = YtDlpPlatformExtractor.KeepAnonymousFailureOverEmptyRetry(anonymous, cookied);

        result.Should().BeSameAs(anonymous);
    }

    [Fact]
    public void KeepAnonymousFailureOverEmptyRetry_CookiedPassFoundMedia_ReturnsIt()
    {
        var anonymous = new Result<MediaPayload, ExtractionError>.Err(new ExtractionError.ToolFailure("HTTP Error 403: Forbidden"));
        var item = new MediaItem("clip.mp4", MediaKind.Video, "video/mp4", 1, null);
        var cookied = Result<MediaPayload, ExtractionError>.Success(new MediaPayload(ReelUrl, "title", null, [item], null));

        var result = YtDlpPlatformExtractor.KeepAnonymousFailureOverEmptyRetry(anonymous, cookied);

        result.Should().BeSameAs(cookied);
    }
}
