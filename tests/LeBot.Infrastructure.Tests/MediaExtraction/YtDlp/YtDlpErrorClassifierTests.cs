using LeBot.Infrastructure.MediaExtraction.YtDlp;

namespace LeBot.Infrastructure.Tests.MediaExtraction.YtDlp;

public class YtDlpErrorClassifierTests
{
    [Theory]
    [InlineData("ERROR: Unsupported URL: https://example.com/page")]
    [InlineData("error: unsupported url: https://example.com/page")]
    public void LooksLikeUnsupportedUrl_UnsupportedUrlError_ReturnsTrue(string detail)
    {
        YtDlpErrorClassifier.LooksLikeUnsupportedUrl(detail).Should().BeTrue();
    }

    [Theory]
    [InlineData("ERROR: [TikTok] 123: Video not available")]
    [InlineData("no error output")]
    public void LooksLikeUnsupportedUrl_OtherErrors_ReturnsFalse(string detail)
    {
        YtDlpErrorClassifier.LooksLikeUnsupportedUrl(detail).Should().BeFalse();
    }

    [Theory]
    [InlineData("ERROR: [generic] Got HTTP Error 403 caused by Cloudflare anti-bot challenge; try again with --extractor-args \"generic:impersonate\"")]
    [InlineData("ERROR: [Generic] product-page: Unable to extract video url")]
    public void IsGenericExtractorFailure_GenericExtractorErrors_ReturnsTrue(string detail)
    {
        YtDlpErrorClassifier.IsGenericExtractorFailure(detail).Should().BeTrue();
    }

    [Theory]
    [InlineData("ERROR: [TikTok] 123: Video not available")]
    [InlineData("ERROR: [Instagram] Requested content is not available, rate-limit reached")]
    [InlineData("no error output")]
    public void IsGenericExtractorFailure_PlatformExtractorErrors_ReturnsFalse(string detail)
    {
        YtDlpErrorClassifier.IsGenericExtractorFailure(detail).Should().BeFalse();
    }

    [Theory]
    [InlineData("ERROR: [TikTok] 7668674355106270485: Unable to extract universal data for rehydration; please report this issue")]
    [InlineData("ERROR: [TikTok] 7668674355106270485: Unexpected response from webpage request; please report this issue")]
    [InlineData("error: unable to extract universal data for rehydration")]
    public void IsTransientChallengeFailure_TikTokChallengeErrors_ReturnsTrue(string detail)
    {
        YtDlpErrorClassifier.IsTransientChallengeFailure(detail).Should().BeTrue();
    }

    [Theory]
    [InlineData("ERROR: Unsupported URL: https://example.com/page")]
    [InlineData("ERROR: [TikTok] 123: Video not available")]
    [InlineData("ERROR: [Instagram] Requested content is not available, rate-limit reached")]
    [InlineData("no error output")]
    public void IsTransientChallengeFailure_PermanentErrors_ReturnsFalse(string detail)
    {
        YtDlpErrorClassifier.IsTransientChallengeFailure(detail).Should().BeFalse();
    }
}
