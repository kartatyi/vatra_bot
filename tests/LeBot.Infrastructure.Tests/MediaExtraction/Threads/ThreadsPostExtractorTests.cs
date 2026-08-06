using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LeBot.Domain.Common;
using LeBot.Domain.Media;
using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.MediaExtraction.Threads;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.Tests.MediaExtraction.Threads;

public class ThreadsPostExtractorTests
{
    [Theory]
    [InlineData("https://www.threads.com/@playpadel.camp/post/DaDO9QigB65", true)]
    [InlineData("https://threads.com/@u/post/abc", true)]
    [InlineData("https://www.threads.net/@u/post/abc", true)]
    [InlineData("https://www.threads.com/share/_leUBLSSL/", true)] // app shortlink → 302 to /@u/post/…
    [InlineData("https://www.threads.com/@playpadel.camp", false)]
    [InlineData("https://www.instagram.com/p/abc/", false)]
    [InlineData("https://tiktok.com/@u/video/123", false)]
    public void CanHandle_ScopesToThreadsPosts(string url, bool expected)
    {
        var sut = CreateSut(Serving(page: string.Empty));

        sut.CanHandle(new Uri(url)).Should().Be(expected);
    }

    [Fact]
    public async Task ExtractAsync_CarouselPost_ReturnsBothPhotosWithTheAuthorsCaption()
    {
        var sut = CreateSut(Serving(Fixture("carousel-post.html")));

        var result = await sut.ExtractAsync(Post("DbsHKtBiGxC"), CancellationToken.None);

        var payload = Ok(result);
        payload.Items.Should().HaveCount(2).And.OnlyContain(i => i.Kind == MediaKind.Photo);
        payload.Author.Should().Be("haryahahahah");
        payload.Description.Should().Be("Kinder Joy, One Piece edition");
        Cleanup(payload);
    }

    [Fact]
    public async Task ExtractAsync_VideoPost_ReturnsTheClip()
    {
        var sut = CreateSut(Serving(Fixture("video-post.html")));

        var result = await sut.ExtractAsync(Post("DbnWztIiMQa"), CancellationToken.None);

        var payload = Ok(result);
        payload.Items.Should().ContainSingle().Which.Kind.Should().Be(MediaKind.Video);
        Cleanup(payload);
    }

    [Fact]
    public async Task ExtractAsync_ShareShortlink_ReadsThePostTheRedirectLandedOn()
    {
        // The shortlink names no post; only the redirected URL does.
        var canonical = Post("DbnWztIiMQa");
        var sut = CreateSut(Serving(Fixture("video-post.html"), redirectedTo: canonical));

        var result = await sut.ExtractAsync(new Uri("https://www.threads.com/share/BAYBI9yu9Y/"), CancellationToken.None);

        var payload = Ok(result);
        payload.Items.Should().ContainSingle();
        Cleanup(payload);
    }

    [Fact]
    public async Task ExtractAsync_TextOnlyPost_ReturnsUnsupportedSoTheCardCanServeIt()
    {
        var sut = CreateSut(Serving(Fixture("text-post.html")));

        var result = await sut.ExtractAsync(Post("DbTextOnly1"), CancellationToken.None);

        Declined(result);
    }

    [Fact]
    public async Task ExtractAsync_LoggedOutShell_RecoversViaTheBrowserFallback()
    {
        var browser = BrowserReturning(PayloadBlockOf(Fixture("video-post.html")));
        var sut = CreateSut(Serving(page: "<html><body>logged-out shell</body></html>"), browser);

        var result = await sut.ExtractAsync(Post("DbnWztIiMQa"), CancellationToken.None);

        var payload = Ok(result);
        payload.Items.Should().ContainSingle().Which.Kind.Should().Be(MediaKind.Video);
        Cleanup(payload);
    }

    [Fact]
    public async Task ExtractAsync_NoBrowserEither_ReturnsUnsupported()
    {
        var sut = CreateSut(Serving(page: "<html><body>logged-out shell</body></html>"), BrowserReturning(null));

        var result = await sut.ExtractAsync(Post("DbnWztIiMQa"), CancellationToken.None);

        Declined(result);
    }

    [Fact]
    public async Task ExtractAsync_PageUnavailable_ReturnsUnsupported()
    {
        var sut = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await sut.ExtractAsync(Post("DbnWztIiMQa"), CancellationToken.None);

        Declined(result);
    }

    [Fact]
    public async Task ExtractAsync_MediaExceedsSizeLimit_ReturnsUnsupported()
    {
        var sut = CreateSut(
            request => IsPageRequest(request)
                ? Html(Fixture("video-post.html"))
                : Oversized(),
            maxFileSizeMb: 1);

        var result = await sut.ExtractAsync(Post("DbnWztIiMQa"), CancellationToken.None);

        Declined(result);
    }

    private static Uri Post(string shortcode) => new($"https://www.threads.com/@someone/post/{shortcode}");

    private static MediaPayload Ok(Result<MediaPayload, ExtractionError> result) =>
        result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Ok>().Subject.Value;

    private static void Declined(Result<MediaPayload, ExtractionError> result) =>
        result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Err>()
            .Which.Error.Should().BeOfType<ExtractionError.UnsupportedPlatform>();

    private static bool IsPageRequest(HttpRequestMessage request) =>
        request.RequestUri!.Host.EndsWith("threads.com", StringComparison.Ordinal);

    /// <summary>Answers the page request with <paramref name="page"/> and any other request with media bytes.</summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> Serving(string page, Uri? redirectedTo = null) =>
        request =>
        {
            if (!IsPageRequest(request))
            {
                return MediaBytes();
            }

            var response = Html(page);
            if (redirectedTo is not null)
            {
                response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, redirectedTo);
            }

            return response;
        };

    private static HttpResponseMessage Html(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/html"),
    };

    private static HttpResponseMessage MediaBytes() => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent([0, 0, 0, 0x20, 0x66, 0x74, 0x79, 0x70])
        {
            Headers = { ContentType = new MediaTypeHeaderValue("video/mp4") },
        },
    };

    private static HttpResponseMessage Oversized()
    {
        var content = new ByteArrayContent([0, 0, 0, 0x20])
        {
            Headers = { ContentType = new MediaTypeHeaderValue("video/mp4") },
        };
        content.Headers.ContentLength = 80_000_000; // 80 MB, over the 1 MB cap the test sets
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static IBrowserPayloadLoader BrowserReturning(string? payload)
    {
        var loader = Substitute.For<IBrowserPayloadLoader>();
        loader.LoadPostPayloadAsync(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(payload));
        return loader;
    }

    /// <summary>The single JSON block the browser fallback hands back, lifted out of a fixture page.</summary>
    private static string PayloadBlockOf(string html)
    {
        var start = html.IndexOf('{', html.IndexOf("<script", StringComparison.Ordinal));
        var end = html.IndexOf("</script>", start, StringComparison.Ordinal);
        return html[start..end];
    }

    private static ThreadsPostExtractor CreateSut(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        IBrowserPayloadLoader? browser = null,
        int maxFileSizeMb = 50)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new StubHttpHandler(respond)));

        var options = Options.Create(new YtDlpOptions
        {
            DownloadDirectory = Path.Combine(Path.GetTempPath(), "lebot-tests-threads"),
            MaxFileSizeMb = maxFileSizeMb,
        });

        return new ThreadsPostExtractor(
            factory,
            browser ?? BrowserReturning(null),
            options,
            NullLogger<ThreadsPostExtractor>.Instance);
    }

    private static string Fixture(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "_fixtures", "threads", fileName));

    private static void Cleanup(MediaPayload payload)
    {
        foreach (var item in payload.Items)
        {
            if (File.Exists(item.FilePath))
            {
                File.Delete(item.FilePath);
            }
        }
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
