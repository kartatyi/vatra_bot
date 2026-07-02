using System.Net;
using System.Net.Http.Headers;
using LeBot.Domain.Common;
using LeBot.Domain.Media;
using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.MediaExtraction.Instagram;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.Tests.MediaExtraction.Instagram;

public class InstagramApiExtractorTests
{
    [Theory]
    [InlineData("https://www.instagram.com/p/DaGKRNaCLiX/", true)]
    [InlineData("https://www.instagram.com/p/DaGKRNaCLiX/?img_index=3", true)]
    [InlineData("https://instagram.com/p/abc/", true)]
    [InlineData("https://www.instagram.com/tv/abc/", true)]
    [InlineData("https://www.instagram.com/reel/abc/", false)]
    [InlineData("https://www.instagram.com/user/", false)]
    [InlineData("https://www.instagram.com/stories/user/12345/", false)]
    [InlineData("https://tiktok.com/p/abc/", false)]
    public void CanHandle_ScopesToInstagramPostsAndTv(string url, bool expected)
    {
        var sut = CreateSut(_ => Json("{\"items\":[]}"), CookieProviderReturning(SomeCookies()));

        sut.CanHandle(new Uri(url)).Should().Be(expected);
    }

    [Fact]
    public async Task ExtractAsync_Carousel_DownloadsEveryPhotoWithCaptionAndAuthor()
    {
        const string json =
            """
            {
              "items": [{
                "media_type": 8,
                "user": { "username": "kyiv_photog" },
                "caption": { "text": "Three frames from Podil" },
                "carousel_media": [
                  { "media_type": 1, "image_versions2": { "candidates": [ { "url": "https://cdn.test/a.jpg", "width": 1080 } ] } },
                  { "media_type": 1, "image_versions2": { "candidates": [ { "url": "https://cdn.test/b.jpg" } ] } },
                  { "media_type": 1, "image_versions2": { "candidates": [ { "url": "https://cdn.test/c.jpg" } ] } }
                ]
              }]
            }
            """;

        var downloaded = new List<string>();
        var sut = CreateSut(request => Respond(request, json, downloaded), CookieProviderReturning(SomeCookies()));

        var result = await sut.ExtractAsync(new Uri("https://www.instagram.com/p/DaGKRNaCLiX/?img_index=3"), CancellationToken.None);

        var ok = result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Ok>().Subject;
        ok.Value.Items.Should().HaveCount(3);
        ok.Value.Items.Should().AllSatisfy(i => i.Kind.Should().Be(MediaKind.Photo));
        ok.Value.Author.Should().Be("kyiv_photog");
        ok.Value.Description.Should().Be("Three frames from Podil");
        downloaded.Should().HaveCount(3);
        Cleanup(ok.Value);
    }

    [Fact]
    public async Task ExtractAsync_SingleImagePost_ReturnsOnePhoto()
    {
        const string json =
            """
            {
              "items": [{
                "media_type": 1,
                "user": { "username": "u" },
                "image_versions2": { "candidates": [ { "url": "https://cdn.test/single.jpg" } ] }
              }]
            }
            """;

        var sut = CreateSut(request => Respond(request, json, null), CookieProviderReturning(SomeCookies()));

        var result = await sut.ExtractAsync(new Uri("https://www.instagram.com/p/abc/"), CancellationToken.None);

        var ok = result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Ok>().Subject;
        ok.Value.Items.Should().ContainSingle().Which.Kind.Should().Be(MediaKind.Photo);
        Cleanup(ok.Value);
    }

    [Fact]
    public async Task ExtractAsync_VideoPost_PrefersVideoVersion()
    {
        const string json =
            """
            {
              "items": [{
                "media_type": 2,
                "video_versions": [ { "url": "https://cdn.test/clip.mp4", "width": 720 } ],
                "image_versions2": { "candidates": [ { "url": "https://cdn.test/thumb.jpg" } ] }
              }]
            }
            """;

        var sut = CreateSut(request => Respond(request, json, null), CookieProviderReturning(SomeCookies()));

        var result = await sut.ExtractAsync(new Uri("https://www.instagram.com/p/abc/"), CancellationToken.None);

        var ok = result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Ok>().Subject;
        ok.Value.Items.Should().ContainSingle().Which.Kind.Should().Be(MediaKind.Video);
        Cleanup(ok.Value);
    }

    [Fact]
    public async Task ExtractAsync_NoCookiesConfigured_ReturnsEmptyPayloadWithoutCallingApi()
    {
        var apiCalled = false;
        var sut = CreateSut(
            request =>
            {
                apiCalled = true;
                return Json("{\"items\":[]}");
            },
            CookieProviderReturning(null));

        var result = await sut.ExtractAsync(new Uri("https://www.instagram.com/p/abc/"), CancellationToken.None);

        var ok = result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Ok>().Subject;
        ok.Value.HasMedia.Should().BeFalse();
        apiCalled.Should().BeFalse();
    }

    [Fact]
    public async Task ExtractAsync_ApiForbidden_ReturnsContentUnavailable()
    {
        var sut = CreateSut(
            _ => new HttpResponseMessage(HttpStatusCode.Forbidden),
            CookieProviderReturning(SomeCookies()));

        var result = await sut.ExtractAsync(new Uri("https://www.instagram.com/p/abc/"), CancellationToken.None);

        result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Err>()
            .Which.Error.Should().BeOfType<ExtractionError.ContentUnavailable>();
    }

    [Fact]
    public async Task ExtractAsync_StaleSession_RefreshesCookiesThenSucceeds()
    {
        const string json =
            """
            { "items": [{ "media_type": 1, "image_versions2": { "candidates": [ { "url": "https://cdn.test/x.jpg" } ] } }] }
            """;

        var apiCalls = 0;
        var sut = CreateSut(
            request =>
            {
                if (request.RequestUri!.AbsoluteUri.Contains("/api/v1/media/", StringComparison.OrdinalIgnoreCase))
                {
                    // First hit: stale session -> 401. After the refresh, serve the post.
                    return ++apiCalls == 1 ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : Json(json);
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0])
                    {
                        Headers = { ContentType = new MediaTypeHeaderValue("image/jpeg") },
                    },
                };
            },
            CookieProviderReturning(SomeCookies()));

        var result = await sut.ExtractAsync(new Uri("https://www.instagram.com/p/abc/"), CancellationToken.None);

        var ok = result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Ok>().Subject;
        ok.Value.Items.Should().ContainSingle();
        apiCalls.Should().Be(2);
        Cleanup(ok.Value);
    }

    [Fact]
    public async Task ExtractAsync_EmptyItems_ReturnsEmptyPayload()
    {
        var sut = CreateSut(_ => Json("{\"items\":[]}"), CookieProviderReturning(SomeCookies()));

        var result = await sut.ExtractAsync(new Uri("https://www.instagram.com/p/abc/"), CancellationToken.None);

        var ok = result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Ok>().Subject;
        ok.Value.HasMedia.Should().BeFalse();
    }

    [Fact]
    public async Task ExtractAsync_MediaExceedsSizeLimit_IsSkipped()
    {
        const string json =
            """
            { "items": [{ "media_type": 1, "image_versions2": { "candidates": [ { "url": "https://cdn.test/big.jpg" } ] } }] }
            """;

        var sut = CreateSut(
            request =>
            {
                if (request.RequestUri!.AbsoluteUri.Contains("/api/v1/media/", StringComparison.OrdinalIgnoreCase))
                {
                    return Json(json);
                }

                var content = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0])
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("image/jpeg") },
                };
                content.Headers.ContentLength = 5_000_000; // 5 MB, over the 1 MB cap below
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            },
            CookieProviderReturning(SomeCookies()),
            maxFileSizeMb: 1);

        var result = await sut.ExtractAsync(new Uri("https://www.instagram.com/p/abc/"), CancellationToken.None);

        var ok = result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Ok>().Subject;
        ok.Value.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_OversizedStreamWithoutContentLength_IsDiscardedMidStream()
    {
        const string json =
            """
            { "items": [{ "media_type": 1, "image_versions2": { "candidates": [ { "url": "https://cdn.test/huge.jpg" } ] } }] }
            """;

        var sut = CreateSut(
            request =>
            {
                if (request.RequestUri!.AbsoluteUri.Contains("/api/v1/media/", StringComparison.OrdinalIgnoreCase))
                {
                    return Json(json);
                }

                // No Content-Length (chunked), body over the 1 MB cap -> only the streaming guard catches it.
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new UnsizedContent(2 * 1024 * 1024, "image/jpeg"),
                };
            },
            CookieProviderReturning(SomeCookies()),
            maxFileSizeMb: 1);

        var result = await sut.ExtractAsync(new Uri("https://www.instagram.com/p/abc/"), CancellationToken.None);

        var ok = result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Ok>().Subject;
        ok.Value.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_UnparseableJson_ReturnsToolFailure()
    {
        var sut = CreateSut(_ => Json("definitely not json {"), CookieProviderReturning(SomeCookies()));

        var result = await sut.ExtractAsync(new Uri("https://www.instagram.com/p/abc/"), CancellationToken.None);

        result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Err>()
            .Which.Error.Should().BeOfType<ExtractionError.ToolFailure>();
    }

    [Fact]
    public async Task ExtractAsync_HttpThrows_ReturnsNetworkFailure()
    {
        var sut = CreateSut(
            _ => throw new HttpRequestException("connection reset"),
            CookieProviderReturning(SomeCookies()));

        var result = await sut.ExtractAsync(new Uri("https://www.instagram.com/p/abc/"), CancellationToken.None);

        result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Err>()
            .Which.Error.Should().BeOfType<ExtractionError.NetworkFailure>();
    }

    private static HttpResponseMessage Respond(
        HttpRequestMessage request,
        string apiJson,
        List<string>? downloaded)
    {
        if (request.RequestUri!.AbsoluteUri.Contains("/api/v1/media/", StringComparison.OrdinalIgnoreCase))
        {
            return Json(apiJson);
        }

        downloaded?.Add(request.RequestUri.AbsoluteUri);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0]) // tiny JPEG header
            {
                Headers = { ContentType = new MediaTypeHeaderValue("image/jpeg") },
            },
        };
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    private static InstagramCookies SomeCookies() => new("sid", "ds", "csrf");

    private static IInstagramCookieProvider CookieProviderReturning(InstagramCookies? cookies)
    {
        var provider = Substitute.For<IInstagramCookieProvider>();
        provider.GetAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(cookies));
        return provider;
    }

    private static InstagramApiExtractor CreateSut(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        IInstagramCookieProvider cookieProvider,
        int maxFileSizeMb = 50)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new StubHttpHandler(respond)));

        var options = Options.Create(new YtDlpOptions
        {
            DownloadDirectory = Path.Combine(Path.GetTempPath(), "lebot-tests-ig"),
            MaxFileSizeMb = maxFileSizeMb,
        });

        return new InstagramApiExtractor(factory, cookieProvider, options, NullLogger<InstagramApiExtractor>.Instance);
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

    // Content that reports no length (TryComputeLength returns false), mimicking a chunked CDN
    // response so the predictive size guard is bypassed and only the streaming cap applies.
    private sealed class UnsizedContent : HttpContent
    {
        private readonly long _byteCount;

        public UnsizedContent(long byteCount, string mediaType)
        {
            _byteCount = byteCount;
            Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        }

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            var buffer = new byte[65536];
            var remaining = _byteCount;
            while (remaining > 0)
            {
                var chunk = (int)Math.Min(buffer.Length, remaining);
                await stream.WriteAsync(buffer.AsMemory(0, chunk));
                remaining -= chunk;
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
