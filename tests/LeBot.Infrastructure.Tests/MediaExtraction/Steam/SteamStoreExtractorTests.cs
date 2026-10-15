using System.Net;
using System.Net.Http.Headers;
using LeBot.Domain.Common;
using LeBot.Domain.Media;
using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.MediaExtraction.Steam;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.Tests.MediaExtraction.Steam;

public sealed class SteamStoreExtractorTests : IDisposable
{
    private static readonly Uri StorePage = new("https://store.steampowered.com/app/3949040/RV_There_Yet/");

    private readonly string _downloadDirectory =
        Path.Combine(Path.GetTempPath(), "lebot-tests-steam", Guid.NewGuid().ToString("N"));

    private readonly ISteamTrailerDownloader _trailerDownloader = Substitute.For<ISteamTrailerDownloader>();

    public SteamStoreExtractorTests()
    {
        Directory.CreateDirectory(_downloadDirectory);
        TrailerDownloadWrites(bytes: 4096);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_downloadDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A file still held open by a failed assertion; the temp folder sweep gets it later.
        }
    }

    [Theory]
    [InlineData("https://store.steampowered.com/app/3949040/RV_There_Yet/", true)]
    [InlineData("https://s.team/a/3949040", true)]
    [InlineData("https://store.steampowered.com/bundle/232/", false)]
    [InlineData("https://www.youtube.com/watch?v=abc", false)]
    public void CanHandle_ClaimsOnlyStorePagesOfOneApp(string url, bool expected)
    {
        var sut = CreateSut(RealStore);

        sut.CanHandle(new Uri(url)).Should().Be(expected);
    }

    [Fact]
    public async Task ExtractAsync_AppWithTrailer_PostsTrailerFirstThenThreeScreenshots()
    {
        var sut = CreateSut(RealStore);

        var result = await sut.ExtractAsync(StorePage, CancellationToken.None);

        var payload = result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Ok>().Subject.Value;
        payload.Items.Select(i => i.Kind).Should().Equal(MediaKind.Video, MediaKind.Photo, MediaKind.Photo, MediaKind.Photo);
        payload.Items[0].DurationSeconds.Should().Be(82);
    }

    [Fact]
    public async Task ExtractAsync_AppWithTrailer_CaptionsWithStoreFactsAndCreditsTheDeveloper()
    {
        var sut = CreateSut(RealStore);

        var result = await sut.ExtractAsync(StorePage, CancellationToken.None);

        var payload = result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Ok>().Subject.Value;
        payload.Description.Should().StartWith("🎮 RV There Yet?\n\n💵 187₴ → 112₴ (−40%)\n📅 21 жовт. 2025\n");
        payload.Description.Should().Contain("⭐ дуже схвальні — 90%");
        payload.Author.Should().Be("Nuggets Entertainment");
    }

    [Fact]
    public async Task ExtractAsync_FullUploadLimit_DownloadsTheTrailerAt720p()
    {
        var sut = CreateSut(RealStore);

        await sut.ExtractAsync(StorePage, CancellationToken.None);

        await _trailerDownloader.Received(1).DownloadAsync(
            new Uri("https://video.akamai.steamstatic.com/store_trailers/3949040/1309387426/dce3fd504257a257abfe259fa3207a0bcf499582/1789049802/dash_h264.mpd?t=1789134288"),
            720,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_TighterUploadLimit_StepsTheTrailerDownTo480p()
    {
        var sut = CreateSut(RealStore, maxFileSizeMb: 25);

        await sut.ExtractAsync(StorePage, CancellationToken.None);

        await _trailerDownloader.Received(1).DownloadAsync(Arg.Any<Uri>(), 480, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_TrailerTooLongForAnyRendition_PostsScreenshotsWithoutDownloadingIt()
    {
        var sut = CreateSut(RealStore, maxFileSizeMb: 10);

        var result = await sut.ExtractAsync(StorePage, CancellationToken.None);

        var payload = result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Ok>().Subject.Value;
        payload.Items.Should().HaveCount(3).And.AllSatisfy(i => i.Kind.Should().Be(MediaKind.Photo));
        await _trailerDownloader.DidNotReceiveWithAnyArgs().DownloadAsync(default!, default, default);
    }

    [Fact]
    public async Task ExtractAsync_TrailerDownloadFails_StillPostsScreenshotsAndCaption()
    {
        _trailerDownloader.DownloadAsync(Arg.Any<Uri>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result<string, ExtractionError>.Failure(new ExtractionError.ToolFailure("ffmpeg not found")));
        var sut = CreateSut(RealStore);

        var result = await sut.ExtractAsync(StorePage, CancellationToken.None);

        var payload = result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Ok>().Subject.Value;
        payload.Items.Should().HaveCount(3).And.AllSatisfy(i => i.Kind.Should().Be(MediaKind.Photo));
        payload.Description.Should().StartWith("🎮 RV There Yet?");
    }

    [Fact]
    public async Task ExtractAsync_TrailerComesOutOverItsBudget_IsDiscardedAndDeleted()
    {
        TrailerDownloadWrites(bytes: 20L * 1024 * 1024);
        var sut = CreateSut(RealStore, maxFileSizeMb: 20);

        var result = await sut.ExtractAsync(StorePage, CancellationToken.None);

        var payload = result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Ok>().Subject.Value;
        payload.Items.Should().NotContain(i => i.Kind == MediaKind.Video);
        Directory.EnumerateFiles(_downloadDirectory, "trailer_*").Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_AppWithoutTrailer_PostsFourScreenshots()
    {
        var screenshots = string.Join(",", Enumerable.Range(1, 6).Select(i => $$"""{ "path_full": "https://shared.test/{{i}}.jpg" }"""));
        var sut = CreateSut(Store(App($""", "screenshots": [{screenshots}]""")));

        var result = await sut.ExtractAsync(StorePage, CancellationToken.None);

        var payload = result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Ok>().Subject.Value;
        payload.Items.Should().HaveCount(4).And.AllSatisfy(i => i.Kind.Should().Be(MediaKind.Photo));
    }

    [Fact]
    public async Task ExtractAsync_AppWithOnlyAHeaderImage_PostsTheHeader()
    {
        var requested = new List<string>();
        var sut = CreateSut(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".jpg", StringComparison.Ordinal))
            {
                requested.Add(request.RequestUri.AbsoluteUri);
            }

            return Store(App(""", "header_image": "https://shared.test/header.jpg" """))(request);
        });

        var result = await sut.ExtractAsync(StorePage, CancellationToken.None);

        result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Ok>().Which.Value.Items.Should().ContainSingle();
        requested.Should().Equal("https://shared.test/header.jpg");
    }

    [Fact]
    public async Task ExtractAsync_NoMediaDownloads_KeepsTheCaptionForATextReply()
    {
        var sut = CreateSut(request => request.RequestUri!.AbsolutePath.EndsWith(".jpg", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Store(App(""", "header_image": "https://shared.test/header.jpg" """))(request));

        var result = await sut.ExtractAsync(StorePage, CancellationToken.None);

        var payload = result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Ok>().Subject.Value;
        payload.HasMedia.Should().BeFalse();
        payload.Description.Should().StartWith("🎮 RV There Yet?").And.EndWith("Drive home.");
    }

    [Fact]
    public async Task ExtractAsync_ReviewsEndpointFails_CaptionGoesWithoutAVerdict()
    {
        var sut = CreateSut(request => request.RequestUri!.AbsolutePath.StartsWith("/appreviews/", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.Forbidden)
            : RealStore(request));

        var result = await sut.ExtractAsync(StorePage, CancellationToken.None);

        var payload = result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Ok>().Subject.Value;
        payload.Description.Should().StartWith("🎮 RV There Yet?").And.NotContain("⭐");
    }

    [Fact]
    public async Task ExtractAsync_AnyLink_AsksTheConfiguredStorefront()
    {
        string? detailsQuery = null;
        var sut = CreateSut(
            request =>
            {
                if (request.RequestUri!.AbsolutePath == "/api/appdetails")
                {
                    detailsQuery = request.RequestUri.Query;
                }

                return RealStore(request);
            },
            steam: new SteamOptions { CountryCode = "pl", Language = "polish" });

        await sut.ExtractAsync(new Uri("https://s.team/a/3949040"), CancellationToken.None);

        detailsQuery.Should().Be("?appids=3949040&cc=pl&l=polish");
    }

    [Fact]
    public async Task ExtractAsync_StoreHasNoPageForTheApp_ReturnsContentUnavailable()
    {
        var sut = CreateSut(_ => Json("""{ "3949040": { "success": false } }"""));

        var result = await sut.ExtractAsync(StorePage, CancellationToken.None);

        result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Err>()
            .Which.Error.Should().BeOfType<ExtractionError.ContentUnavailable>();
    }

    [Fact]
    public async Task ExtractAsync_StoreApiRefuses_ReturnsNetworkFailure()
    {
        var sut = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var result = await sut.ExtractAsync(StorePage, CancellationToken.None);

        result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Err>()
            .Which.Error.Should().BeOfType<ExtractionError.NetworkFailure>();
    }

    [Fact]
    public async Task ExtractAsync_StoreUnreachable_ReturnsNetworkFailure()
    {
        var sut = CreateSut(_ => throw new HttpRequestException("connection reset"));

        var result = await sut.ExtractAsync(StorePage, CancellationToken.None);

        result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Err>()
            .Which.Error.Should().BeOfType<ExtractionError.NetworkFailure>();
    }

    [Fact]
    public async Task ExtractAsync_StoreApiAnswersWithHtml_ReturnsToolFailure()
    {
        var sut = CreateSut(_ => Json("<html>Access Denied</html>"));

        var result = await sut.ExtractAsync(StorePage, CancellationToken.None);

        result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Err>()
            .Which.Error.Should().BeOfType<ExtractionError.ToolFailure>();
    }

    [Fact]
    public async Task ExtractAsync_NotAStorePage_ReturnsUnsupportedPlatform()
    {
        var sut = CreateSut(RealStore);

        var result = await sut.ExtractAsync(new Uri("https://store.steampowered.com/bundle/232/"), CancellationToken.None);

        result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Err>()
            .Which.Error.Should().BeOfType<ExtractionError.UnsupportedPlatform>();
    }

    /// <summary>The captured store: RV There Yet?'s details and reviews, and YAPYAP's trailer manifest.</summary>
    private static HttpResponseMessage RealStore(HttpRequestMessage request) =>
        Store(SteamFixtures.AppDetails(SteamFixtures.DiscountedAppId))(request);

    private static Func<HttpRequestMessage, HttpResponseMessage> Store(string appDetailsJson) => request =>
    {
        var path = request.RequestUri!.AbsolutePath;
        return path switch
        {
            "/api/appdetails" => Json(appDetailsJson),
            _ when path.StartsWith("/appreviews/", StringComparison.Ordinal) => Json(SteamFixtures.Reviews()),
            _ when path.EndsWith(".mpd", StringComparison.Ordinal) => Text(SteamFixtures.TrailerManifest(), "application/dash+xml"),
            _ => Jpeg(),
        };
    };

    private static string App(string mediaFields) =>
        $$"""{ "3949040": { "success": true, "data": { "name": "RV There Yet?", "short_description": "Drive home."{{mediaFields}} } } }""";

    private void TrailerDownloadWrites(long bytes) =>
        _trailerDownloader.DownloadAsync(Arg.Any<Uri>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var path = Path.Combine(_downloadDirectory, $"trailer_{Guid.NewGuid():N}.mp4");
                using (var file = File.Create(path))
                {
                    file.SetLength(bytes);
                }

                return Result<string, ExtractionError>.Success(path);
            });

    private static HttpResponseMessage Json(string body) => Text(body, "application/json");

    private static HttpResponseMessage Text(string body, string mediaType) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, mediaType),
    };

    private static HttpResponseMessage Jpeg() => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0])
        {
            Headers = { ContentType = new MediaTypeHeaderValue("image/jpeg") },
        },
    };

    private SteamStoreExtractor CreateSut(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        int maxFileSizeMb = 50,
        SteamOptions? steam = null)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new StubHttpHandler(respond)));

        return new SteamStoreExtractor(
            factory,
            _trailerDownloader,
            Options.Create(steam ?? new SteamOptions()),
            Options.Create(new YtDlpOptions { DownloadDirectory = _downloadDirectory, MaxFileSizeMb = maxFileSizeMb }),
            NullLogger<SteamStoreExtractor>.Instance);
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
