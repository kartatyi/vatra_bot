using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LeBot.Domain.Media;
using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.MediaExtraction.Threads;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.Tests.MediaExtraction.Threads;

public class ThreadsCardLoaderTests
{
    private const string PageWithCard =
        """<html><head><meta property="og:image" content="https://scontent.test/card.jpg?a=1&amp;b=2" /></head></html>""";

    [Fact]
    public async Task LoadAsync_PageWithAnOgImage_DownloadsItAsAPhoto()
    {
        var sut = CreateSut(request => IsPageRequest(request) ? Html(PageWithCard) : Jpeg());

        var card = await sut.LoadAsync(Post(), CancellationToken.None);

        card!.Kind.Should().Be(MediaKind.Photo);
        card.MimeType.Should().Be("image/jpeg");
        File.Exists(card.FilePath).Should().BeTrue();
        File.Delete(card.FilePath);
    }

    [Fact]
    public async Task LoadAsync_PageWithoutAnOgImage_ReturnsNull()
    {
        var sut = CreateSut(_ => Html("<html><head><title>nothing to see</title></head></html>"));

        var card = await sut.LoadAsync(Post(), CancellationToken.None);

        card.Should().BeNull();
    }

    [Fact]
    public async Task LoadAsync_PageUnavailable_ReturnsNull()
    {
        var sut = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var card = await sut.LoadAsync(Post(), CancellationToken.None);

        card.Should().BeNull();
    }

    // The whole trick: Threads renders the card for crawlers only. A browser UA gets the shell.
    [Fact]
    public async Task LoadAsync_AsksAsACrawler()
    {
        string? userAgent = null;
        var sut = CreateSut(request =>
        {
            if (IsPageRequest(request))
            {
                userAgent = request.Headers.UserAgent.ToString();
                return Html(PageWithCard);
            }

            return Jpeg();
        });

        var card = await sut.LoadAsync(Post(), CancellationToken.None);

        userAgent.Should().Be("Twitterbot/1.0");
        File.Delete(card!.FilePath);
    }

    private static Uri Post() => new("https://www.threads.com/@someone/post/DbTextOnly1");

    private static bool IsPageRequest(HttpRequestMessage request) =>
        request.RequestUri!.Host.EndsWith("threads.com", StringComparison.Ordinal);

    private static HttpResponseMessage Html(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/html"),
    };

    private static HttpResponseMessage Jpeg() => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0])
        {
            Headers = { ContentType = new MediaTypeHeaderValue("image/jpeg") },
        },
    };

    private static ThreadsCardLoader CreateSut(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new StubHttpHandler(respond)));

        var options = Options.Create(new YtDlpOptions
        {
            DownloadDirectory = Path.Combine(Path.GetTempPath(), "lebot-tests-threads-card"),
        });

        return new ThreadsCardLoader(factory, options, NullLogger<ThreadsCardLoader>.Instance);
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
