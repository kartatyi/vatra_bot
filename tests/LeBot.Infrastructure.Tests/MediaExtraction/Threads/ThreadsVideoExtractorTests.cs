using System.Net;
using System.Net.Http.Headers;
using LeBot.Domain.Common;
using LeBot.Domain.Media;
using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.MediaExtraction.Threads;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.Tests.MediaExtraction.Threads;

public class ThreadsVideoExtractorTests
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
        var sut = CreateSut(ResolverReturning(null), _ => Json("{}"));

        sut.CanHandle(new Uri(url)).Should().Be(expected);
    }

    [Fact]
    public async Task ExtractAsync_VideoResolved_ReturnsSingleVideoItem()
    {
        var resolver = ResolverReturning("https://cdn.test/clip.mp4");
        var sut = CreateSut(resolver, ServeVideoBytes);

        var result = await sut.ExtractAsync(Post(), CancellationToken.None);

        var ok = result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Ok>().Subject;
        ok.Value.Items.Should().ContainSingle().Which.Kind.Should().Be(MediaKind.Video);
        Cleanup(ok.Value);
    }

    [Fact]
    public async Task ExtractAsync_NoVideoOnPage_ReturnsUnsupportedSoEmbedCanRun()
    {
        var sut = CreateSut(ResolverReturning(null), ServeVideoBytes);

        var result = await sut.ExtractAsync(Post(), CancellationToken.None);

        result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Err>()
            .Which.Error.Should().BeOfType<ExtractionError.UnsupportedPlatform>();
    }

    [Fact]
    public async Task ExtractAsync_ResolverThrows_ReturnsUnsupported()
    {
        var resolver = Substitute.For<IBrowserVideoResolver>();
        resolver.ResolveVideoUrlAsync(Arg.Any<Uri>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string?>(new InvalidOperationException("boom")));
        var sut = CreateSut(resolver, ServeVideoBytes);

        var result = await sut.ExtractAsync(Post(), CancellationToken.None);

        result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Err>()
            .Which.Error.Should().BeOfType<ExtractionError.UnsupportedPlatform>();
    }

    [Fact]
    public async Task ExtractAsync_VideoExceedsSizeLimit_ReturnsUnsupported()
    {
        var resolver = ResolverReturning("https://cdn.test/big.mp4");
        var sut = CreateSut(
            resolver,
            _ =>
            {
                var content = new ByteArrayContent([0, 0, 0, 0x20])
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("video/mp4") },
                };
                content.Headers.ContentLength = 80_000_000; // 80 MB, over the 1 MB cap below
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            },
            maxFileSizeMb: 1);

        var result = await sut.ExtractAsync(Post(), CancellationToken.None);

        result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Err>()
            .Which.Error.Should().BeOfType<ExtractionError.UnsupportedPlatform>();
    }

    [Fact]
    public async Task ExtractAsync_DownloadForbidden_ReturnsUnsupported()
    {
        var resolver = ResolverReturning("https://cdn.test/clip.mp4");
        var sut = CreateSut(resolver, _ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var result = await sut.ExtractAsync(Post(), CancellationToken.None);

        result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Err>()
            .Which.Error.Should().BeOfType<ExtractionError.UnsupportedPlatform>();
    }

    private static Uri Post() => new("https://www.threads.com/@playpadel.camp/post/DaDO9QigB65");

    private static HttpResponseMessage ServeVideoBytes(HttpRequestMessage _) =>
        new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([0, 0, 0, 0x20, 0x66, 0x74, 0x79, 0x70]) // tiny mp4 ftyp header
            {
                Headers = { ContentType = new MediaTypeHeaderValue("video/mp4") },
            },
        };

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    private static IBrowserVideoResolver ResolverReturning(string? url)
    {
        var resolver = Substitute.For<IBrowserVideoResolver>();
        resolver.ResolveVideoUrlAsync(Arg.Any<Uri>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(url));
        return resolver;
    }

    private static ThreadsVideoExtractor CreateSut(
        IBrowserVideoResolver resolver,
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        int maxFileSizeMb = 50)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new StubHttpHandler(respond)));

        var options = Options.Create(new YtDlpOptions
        {
            DownloadDirectory = Path.Combine(Path.GetTempPath(), "lebot-tests-threads"),
            MaxFileSizeMb = maxFileSizeMb,
        });

        return new ThreadsVideoExtractor(resolver, factory, options, NullLogger<ThreadsVideoExtractor>.Instance);
    }

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
