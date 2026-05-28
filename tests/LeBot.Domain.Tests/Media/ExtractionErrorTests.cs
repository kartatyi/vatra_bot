using LeBot.Domain.Media;

namespace LeBot.Domain.Tests.Media;

public class ExtractionErrorTests
{
    [Fact]
    public void UnsupportedPlatform_Reason_MentionsHost()
    {
        var error = new ExtractionError.UnsupportedPlatform(new Uri("https://nope.example.com/foo"));

        error.Reason.Should().Contain("nope.example.com");
    }

    [Fact]
    public void ContentUnavailable_Reason_IncludesUrlAndDetail()
    {
        var url = new Uri("https://tiktok.com/x");

        var error = new ExtractionError.ContentUnavailable(url, "private video");

        error.Reason.Should().Contain(url.ToString());
        error.Reason.Should().Contain("private video");
    }

    [Fact]
    public void NetworkFailure_Reason_IncludesUrlAndDetail()
    {
        var url = new Uri("https://example.com/x");

        var error = new ExtractionError.NetworkFailure(url, "timeout");

        error.Reason.Should().Contain(url.ToString());
        error.Reason.Should().Contain("timeout");
    }

    [Fact]
    public void ToolFailure_Reason_IncludesDetail()
    {
        var error = new ExtractionError.ToolFailure("yt-dlp not found");

        error.Reason.Should().Contain("yt-dlp not found");
    }
}
