using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.MediaExtraction.ThreadsEmbed;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.Tests.MediaExtraction.ThreadsEmbed;

public class ThreadsEmbedExtractorTests
{
    [Theory]
    [InlineData("https://www.threads.com/@natali_02_mr/post/Dbd2dO2jAvC", true)]
    [InlineData("https://threads.com/@u/post/abc", true)]
    [InlineData("https://www.threads.net/@u/post/abc", true)]
    [InlineData("https://www.threads.com/share/_leUBLSSL/", true)] // app shortlink → 302 to /@u/post/…
    [InlineData("https://www.threads.com/@natali_02_mr", false)]
    [InlineData("https://www.instagram.com/p/abc/", false)]
    public void CanHandle_ScopesToThreadsPosts(string url, bool expected)
    {
        var sut = CreateSut();

        sut.CanHandle(new Uri(url)).Should().Be(expected);
    }

    private static ThreadsEmbedExtractor CreateSut()
    {
        var factory = Substitute.For<IHttpClientFactory>();

        var options = Options.Create(new YtDlpOptions
        {
            DownloadDirectory = Path.Combine(Path.GetTempPath(), "lebot-tests-threads-embed"),
        });

        return new ThreadsEmbedExtractor(factory, options, NullLogger<ThreadsEmbedExtractor>.Instance);
    }
}
