using System.Net;
using LeBot.Domain.Common;
using LeBot.Domain.Media;
using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.MediaExtraction.InstagramEmbed;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.Tests.MediaExtraction.InstagramEmbed;

public class InstagramEmbedExtractorTests
{
    [Theory]
    [InlineData("https://www.instagram.com/p/DY4a3nMCtd_/", true)]
    [InlineData("https://instagram.com/p/abc/", true)]
    [InlineData("https://www.instagram.com/p/abc/?igsh=xyz", true)]
    [InlineData("https://www.instagram.com/reel/abc/", false)]
    [InlineData("https://www.instagram.com/user/", false)]
    [InlineData("https://www.instagram.com/stories/user/12345/", false)]
    [InlineData("https://tiktok.com/p/abc/", false)]
    [InlineData("https://youtube.com/shorts/abc", false)]
    public void CanHandle_ScopesToInstagramSlashP(string url, bool expected)
    {
        var sut = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.OK));

        sut.CanHandle(new Uri(url)).Should().Be(expected);
    }

    [Fact]
    public async Task ExtractAsync_HttpFailure_ReturnsNetworkError()
    {
        var sut = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await sut.ExtractAsync(new Uri("https://www.instagram.com/p/abc/"), CancellationToken.None);

        result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Err>()
            .Which.Error.Should().BeOfType<ExtractionError.NetworkFailure>();
    }

    [Fact]
    public async Task ExtractAsync_HtmlWithoutImages_ReturnsEmptyPayload()
    {
        var html = "<html><body>Boring shell, no display_url anywhere.</body></html>";
        var sut = CreateSut(_ => MakeHtmlResponse(html));

        var result = await sut.ExtractAsync(new Uri("https://www.instagram.com/p/abc/"), CancellationToken.None);

        var ok = result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Ok>().Subject;
        ok.Value.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_CaptionPresent_IsSurfacedAsDescription()
    {
        // Mirror the real Instagram embed shape: double-escaped JSON keys with \uXXXX-encoded
        // unicode inside captions. The extractor unwraps both escape layers.
        var html =
            """
            <html><body>
              <script>state = {\"username\":\"saab\",\"caption\":\"Sweden’s Prime Minister announced today.\"}</script>
            </body></html>
            """;

        var sut = CreateSut(_ => MakeHtmlResponse(html));

        var result = await sut.ExtractAsync(new Uri("https://www.instagram.com/p/abc/"), CancellationToken.None);

        var ok = result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Ok>().Subject;
        ok.Value.Description.Should().Contain("Sweden");
        ok.Value.Description.Should().Contain("Prime Minister");
        // The ’ should have been JSON-decoded into the actual right-single-quote glyph.
        ok.Value.Description.Should().Contain("’");
        ok.Value.Author.Should().Be("saab");
    }

    [Fact]
    public async Task ExtractAsync_DisplayUrlsExtracted_AndDeduplicatedByMediaId()
    {
        // Two unique post images (different media IDs) plus a duplicate of the first
        // (same media ID, different signing query). Expect exactly two photos downloaded.
        const string image1Embedded =
            @"https:\\\/\\\/instagram.fiev22-1.fna.fbcdn.net\\\/v\\\/t51.82787-15\\\/707477966_18588937030005372_3256314069446268712_n.jpg?stp=dst-jpg_e35_p1080x1080&_nc_sid=aaaa";
        const string image1Resigned =
            @"https:\\\/\\\/instagram.fiev22-1.fna.fbcdn.net\\\/v\\\/t51.82787-15\\\/707477966_18588937030005372_3256314069446268712_n.jpg?stp=dst-jpg_e35_p640x640&_nc_sid=bbbb";
        const string image2Embedded =
            @"https:\\\/\\\/instagram.fiev22-1.fna.fbcdn.net\\\/v\\\/t51.82787-15\\\/706317213_18588937042005372_3762904905822213462_n.jpg?stp=dst-jpg_e35_p1080x1080&_nc_sid=cccc";

        var html =
            $"\"display_url\\\":\\\"{image1Embedded}\\\"\n" +
            $"\"display_url\\\":\\\"{image2Embedded}\\\"\n" +
            $"\"display_url\\\":\\\"{image1Resigned}\\\"";

        var downloadedUrls = new List<string>();

        var sut = CreateSut(request =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("/embed/", StringComparison.OrdinalIgnoreCase))
            {
                return MakeHtmlResponse(html);
            }

            downloadedUrls.Add(request.RequestUri.AbsoluteUri);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0]) // tiny JPEG header bytes
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg") },
                },
            };
        });

        var result = await sut.ExtractAsync(new Uri("https://www.instagram.com/p/abc/"), CancellationToken.None);

        var ok = result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Ok>().Subject;
        ok.Value.Items.Should().HaveCount(2);
        ok.Value.Items.Should().AllSatisfy(i => i.Kind.Should().Be(MediaKind.Photo));
        downloadedUrls.Should().HaveCount(2);

        // Cleanup downloaded files.
        foreach (var item in ok.Value.Items)
        {
            if (File.Exists(item.FilePath))
            {
                File.Delete(item.FilePath);
            }
        }
    }

    [Fact]
    public async Task ExtractAsync_EmbedHtmlBuildsCaptionedUrlFromInputPath()
    {
        Uri? observedRequestUri = null;
        var sut = CreateSut(request =>
        {
            observedRequestUri = request.RequestUri;
            return MakeHtmlResponse("<html></html>");
        });

        await sut.ExtractAsync(
            new Uri("https://www.instagram.com/p/DY4a3nMCtd_/?igsh=xyz"),
            CancellationToken.None);

        observedRequestUri.Should().NotBeNull();
        observedRequestUri!.AbsolutePath.Should().Be("/p/DY4a3nMCtd_/embed/captioned/");
        observedRequestUri.Host.Should().Be("www.instagram.com");
    }

    private static InstagramEmbedExtractor CreateSut(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new StubHttpHandler(respond)));

        var options = Options.Create(new YtDlpOptions
        {
            DownloadDirectory = Path.Combine(Path.GetTempPath(), "lebot-tests"),
            MaxFileSizeMb = 50,
        });

        return new InstagramEmbedExtractor(factory, options, NullLogger<InstagramEmbedExtractor>.Instance);
    }

    private static HttpResponseMessage MakeHtmlResponse(string html) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html"),
    };

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
