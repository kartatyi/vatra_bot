using System.Net;
using System.Net.Http.Headers;
using LeBot.Domain.Common;
using LeBot.Domain.Media;
using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.MediaExtraction.TikTok;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.Tests.MediaExtraction.TikTok;

public class TikTokMirrorExtractorTests
{
    private const string PrimaryMirror = "https://mirror.test/api/";
    private const string SecondaryMirror = "https://backup.test/api/";

    [Theory]
    [InlineData("https://www.tiktok.com/@user/video/7672043058082368800", true)]
    [InlineData("https://tiktok.com/@user/video/123", true)]
    [InlineData("https://vt.tiktok.com/ZS438r9VU/", true)]
    [InlineData("https://vm.tiktok.com/ZMabc/", true)]
    [InlineData("https://m.tiktok.com/v/123.html", true)]
    [InlineData("https://www.instagram.com/p/abc/", false)]
    [InlineData("https://youtube.com/shorts/abc", false)]
    [InlineData("https://nottiktok.com/@user/video/123", false)]
    public void CanHandle_ScopesToTikTokHosts(string url, bool expected)
    {
        var sut = CreateSut(_ => Json(VideoJson()));

        sut.CanHandle(new Uri(url)).Should().Be(expected);
    }

    [Fact]
    public async Task ExtractAsync_VideoPost_DownloadsClipWithCaptionAndAuthor()
    {
        var sut = CreateSut(request => request.RequestUri!.Host == "mirror.test"
            ? Json(VideoJson())
            : Binary("video/mp4"));

        var result = await sut.ExtractAsync(new Uri("https://vt.tiktok.com/ZS438r9VU/"), CancellationToken.None);

        var ok = result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Ok>().Subject;
        ok.Value.Items.Should().ContainSingle().Which.Kind.Should().Be(MediaKind.Video);
        ok.Value.Items[0].DurationSeconds.Should().Be(19);
        ok.Value.Description.Should().Be("#spiderman #трейлер");
        ok.Value.Author.Should().Be("Інуля");
        Cleanup(ok.Value);
    }

    [Fact]
    public async Task ExtractAsync_HdRenditionPresent_IsPreferredOverStandard()
    {
        var requested = new List<string>();
        var sut = CreateSut(request =>
        {
            if (request.RequestUri!.Host == "mirror.test")
            {
                return Json(
                    """
                    {
                      "code": 0,
                      "data": {
                        "title": "t",
                        "play": "https://cdn.test/sd.mp4",
                        "hdplay": "https://cdn.test/hd.mp4"
                      }
                    }
                    """);
            }

            requested.Add(request.RequestUri.AbsoluteUri);
            return Binary("video/mp4");
        });

        var result = await sut.ExtractAsync(new Uri("https://vt.tiktok.com/abc/"), CancellationToken.None);

        var ok = result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Ok>().Subject;
        requested.Should().ContainSingle().Which.Should().Be("https://cdn.test/hd.mp4");
        Cleanup(ok.Value);
    }

    [Fact]
    public async Task ExtractAsync_PhotoSlideshow_DownloadsEveryImage()
    {
        var sut = CreateSut(request => request.RequestUri!.Host == "mirror.test"
            ? Json(
                """
                {
                  "code": 0,
                  "data": {
                    "title": "three frames",
                    "images": [
                      "https://cdn.test/1.jpg",
                      "https://cdn.test/2.jpg",
                      "https://cdn.test/3.jpg"
                    ]
                  }
                }
                """)
            : Binary("image/jpeg"));

        var result = await sut.ExtractAsync(new Uri("https://vt.tiktok.com/abc/"), CancellationToken.None);

        var ok = result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Ok>().Subject;
        ok.Value.Items.Should().HaveCount(3);
        ok.Value.Items.Should().AllSatisfy(i => i.Kind.Should().Be(MediaKind.Photo));
        Cleanup(ok.Value);
    }

    [Fact]
    public async Task ExtractAsync_SlideshowLongerThanTelegramAlbum_IsCappedAtTenItems()
    {
        var images = string.Join(",", Enumerable.Range(1, 14).Select(i => $"\"https://cdn.test/{i}.jpg\""));
        var sut = CreateSut(request => request.RequestUri!.Host == "mirror.test"
            ? Json($$"""{ "code": 0, "data": { "title": "many", "images": [{{images}}] } }""")
            : Binary("image/jpeg"));

        var result = await sut.ExtractAsync(new Uri("https://vt.tiktok.com/abc/"), CancellationToken.None);

        var ok = result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Ok>().Subject;
        ok.Value.Items.Should().HaveCount(10);
        Cleanup(ok.Value);
    }

    [Fact]
    public async Task ExtractAsync_HostRelativeMediaUrl_ResolvesAgainstMirrorOrigin()
    {
        var requested = new List<string>();
        var sut = CreateSut(request =>
        {
            if (request.RequestUri!.Host == "mirror.test" && request.RequestUri.Query.Length > 0)
            {
                return Json("""{ "code": 0, "data": { "title": "t", "play": "/video/tos/clip.mp4" } }""");
            }

            requested.Add(request.RequestUri.AbsoluteUri);
            return Binary("video/mp4");
        });

        var result = await sut.ExtractAsync(new Uri("https://vt.tiktok.com/abc/"), CancellationToken.None);

        var ok = result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Ok>().Subject;
        requested.Should().ContainSingle().Which.Should().Be("https://mirror.test/video/tos/clip.mp4");
        Cleanup(ok.Value);
    }

    [Fact]
    public async Task ExtractAsync_MediaUrlWithNonHttpScheme_IsSkipped()
    {
        var sut = CreateSut(request => request.RequestUri!.Host == "mirror.test"
            ? Json("""{ "code": 0, "data": { "title": "t", "play": "ftp://cdn.test/clip.mp4" } }""")
            : Binary("video/mp4"));

        var result = await sut.ExtractAsync(new Uri("https://vt.tiktok.com/abc/"), CancellationToken.None);

        var ok = result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Ok>().Subject;
        ok.Value.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_FallbackDisabled_DisownsUrlWithoutCallingMirror()
    {
        var mirrorCalled = false;
        var sut = CreateSut(
            _ =>
            {
                mirrorCalled = true;
                return Json(VideoJson());
            },
            enabled: false);

        var result = await sut.ExtractAsync(new Uri("https://vt.tiktok.com/abc/"), CancellationToken.None);

        result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Err>()
            .Which.Error.Should().BeOfType<ExtractionError.UnsupportedPlatform>();
        mirrorCalled.Should().BeFalse();
    }

    [Fact]
    public async Task ExtractAsync_MirrorReportsNonZeroCode_ReturnsContentUnavailable()
    {
        var sut = CreateSut(_ => Json("""{ "code": -1, "msg": "video not found" }"""));

        var result = await sut.ExtractAsync(new Uri("https://vt.tiktok.com/abc/"), CancellationToken.None);

        result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Err>()
            .Which.Error.Should().BeOfType<ExtractionError.ContentUnavailable>();
    }

    [Fact]
    public async Task ExtractAsync_FirstMirrorRateLimited_SecondMirrorServesTheVideo()
    {
        var sut = CreateSut(
            request => request.RequestUri!.Host switch
            {
                "mirror.test" => Json("""{ "code": -1, "msg": "rate limit" }"""),
                "backup.test" => Json(VideoJson()),
                _ => Binary("video/mp4"),
            },
            endpoints: [PrimaryMirror, SecondaryMirror]);

        var result = await sut.ExtractAsync(new Uri("https://vt.tiktok.com/abc/"), CancellationToken.None);

        var ok = result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Ok>().Subject;
        ok.Value.Items.Should().ContainSingle();
        Cleanup(ok.Value);
    }

    [Fact]
    public async Task ExtractAsync_MirrorReturnsNeitherMediaNorCaption_ReturnsContentUnavailable()
    {
        var sut = CreateSut(_ => Json("""{ "code": 0, "data": { } }"""));

        var result = await sut.ExtractAsync(new Uri("https://vt.tiktok.com/abc/"), CancellationToken.None);

        result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Err>()
            .Which.Error.Should().BeOfType<ExtractionError.ContentUnavailable>();
    }

    [Fact]
    public async Task ExtractAsync_MediaExceedsSizeLimit_IsSkippedLeavingTheCaption()
    {
        var sut = CreateSut(
            request =>
            {
                if (request.RequestUri!.Host == "mirror.test")
                {
                    return Json(VideoJson());
                }

                var content = new ByteArrayContent([0x00, 0x00, 0x00, 0x18])
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("video/mp4") },
                };
                content.Headers.ContentLength = 60 * 1024 * 1024; // 60 MB, over the 50 MB ceiling
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            });

        var result = await sut.ExtractAsync(new Uri("https://vt.tiktok.com/abc/"), CancellationToken.None);

        var ok = result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Ok>().Subject;
        ok.Value.Items.Should().BeEmpty();
        ok.Value.Description.Should().Be("#spiderman #трейлер");
    }

    [Fact]
    public async Task ExtractAsync_HttpThrows_ReturnsNetworkFailure()
    {
        var sut = CreateSut(_ => throw new HttpRequestException("connection reset"));

        var result = await sut.ExtractAsync(new Uri("https://vt.tiktok.com/abc/"), CancellationToken.None);

        result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Err>()
            .Which.Error.Should().BeOfType<ExtractionError.NetworkFailure>();
    }

    [Fact]
    public async Task ExtractAsync_UnparseableJson_ReturnsToolFailure()
    {
        var sut = CreateSut(_ => Json("definitely not json {"));

        var result = await sut.ExtractAsync(new Uri("https://vt.tiktok.com/abc/"), CancellationToken.None);

        result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Err>()
            .Which.Error.Should().BeOfType<ExtractionError.ToolFailure>();
    }

    [Fact]
    public async Task ExtractAsync_NonTikTokUrl_ReturnsUnsupportedPlatform()
    {
        var sut = CreateSut(_ => Json(VideoJson()));

        var result = await sut.ExtractAsync(new Uri("https://example.com/watch"), CancellationToken.None);

        result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Err>()
            .Which.Error.Should().BeOfType<ExtractionError.UnsupportedPlatform>();
    }

    private static string VideoJson() =>
        """
        {
          "code": 0,
          "msg": "success",
          "data": {
            "title": "#spiderman #трейлер",
            "duration": 19,
            "play": "https://cdn.test/clip.mp4",
            "hdplay": "",
            "author": { "unique_id": "inulya", "nickname": "Інуля" }
          }
        }
        """;

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Binary(string mediaType) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent([0x00, 0x00, 0x00, 0x18])
        {
            Headers = { ContentType = new MediaTypeHeaderValue(mediaType) },
        },
    };

    private static TikTokMirrorExtractor CreateSut(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        bool enabled = true,
        IReadOnlyList<string>? endpoints = null)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new StubHttpHandler(respond)));

        var tikTok = Options.Create(new TikTokOptions
        {
            MirrorFallbackEnabled = enabled,
            MirrorEndpoints = endpoints ?? [PrimaryMirror],
            TimeoutSeconds = 5,
        });

        var download = Options.Create(new YtDlpOptions
        {
            DownloadDirectory = Path.Combine(Path.GetTempPath(), "lebot-tests-tt"),
            MaxFileSizeMb = 50,
        });

        return new TikTokMirrorExtractor(factory, tikTok, download, NullLogger<TikTokMirrorExtractor>.Instance);
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
