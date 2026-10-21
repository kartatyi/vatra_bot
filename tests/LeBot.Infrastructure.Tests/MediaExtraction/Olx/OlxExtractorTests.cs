using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using LeBot.Domain.Common;
using LeBot.Domain.Media;
using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.MediaExtraction.Olx;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.Tests.MediaExtraction.Olx;

public class OlxExtractorTests
{
    private const string CdnHost = "ireland.apollo.olxcdn.com";

    private static readonly Uri SolarPlayUrl =
        new("https://www.olx.ua/d/uk/obyavlenie/solarplay-q2501-2400w-2160wh-lifepo4-ID11svhc.html?search_reason=search%7Corganic");

    [Theory]
    [InlineData("https://www.olx.ua/d/uk/obyavlenie/solarplay-q2501-2400w-2160wh-lifepo4-ID11svhc.html?search_reason=search%7Corganic", true)]
    [InlineData("https://www.olx.ua/d/obyavlenie/garmin-vivoactive-3-music-IDXjUJw.html", true)]
    [InlineData("https://m.olx.ua/d/uk/obyavlenie/garmin-vivoactive-3-music-IDXjUJw.html", true)]
    [InlineData("https://olx.ua/d/uk/obyavlenie/garmin-vivoactive-3-music-IDXjUJw.html", true)]
    [InlineData("https://www.olx.ua/uk/obyavlenie/rabota/komplektuvalnik-na-sklad-ID11p23y.html", false)]
    [InlineData("https://www.olx.ua/uk/elektronika/", false)]
    [InlineData("https://www.olx.ua/d/uk/obyavlenie/", false)]
    [InlineData("https://www.olx.pl/d/oferta/rower-gorski-CID767-ID11abcd.html", false)]
    [InlineData("https://notolx.ua/d/uk/obyavlenie/garmin-vivoactive-3-music-IDXjUJw.html", false)]
    public void CanHandle_ScopesToOlxUaAdvertUrls(string url, bool expected)
    {
        var sut = CreateSut(_ => Ok("<html></html>"));

        sut.CanHandle(new Uri(url)).Should().Be(expected);
    }

    [Fact]
    public async Task ExtractAsync_Advert_RepostsTheGalleryUnderTheCaption()
    {
        var html = LoadFixture("solarplay_q2501_ID11svhc.html");
        var sut = CreateSut(request => request.RequestUri!.Host == CdnHost ? Jpeg([0xFF, 0xD8]) : Ok(html));

        var result = await sut.ExtractAsync(SolarPlayUrl, CancellationToken.None);

        var payload = result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Ok>().Subject.Value;
        payload.Items.Should().HaveCount(2).And.AllSatisfy(i => i.Kind.Should().Be(MediaKind.Photo));
        payload.Description.Should().StartWith("🏷 SolarPlay Q2501 2400W (2160Wh) LiFePO4\n💵 29 500 грн.\n📍 Київ, Дарницький");
        payload.Description.Should().Contain("• Стан: Нове");
        Cleanup(payload);
    }

    [Fact]
    public async Task ExtractAsync_Photos_AreAskedForAsJpeg()
    {
        var html = LoadFixture("solarplay_q2501_ID11svhc.html");
        var accepts = new ConcurrentBag<string>();
        var sut = CreateSut(request =>
        {
            if (request.RequestUri!.Host != CdnHost)
            {
                return Ok(html);
            }

            accepts.Add(request.Headers.Accept.ToString());
            return Jpeg([0xFF, 0xD8]);
        });

        var result = await sut.ExtractAsync(SolarPlayUrl, CancellationToken.None);

        accepts.Should().HaveCount(2).And.AllBe("image/jpeg");
        Cleanup(((Result<MediaPayload, ExtractionError>.Ok)result).Value);
    }

    [Fact]
    public async Task ExtractAsync_PhotoThatArrivesLast_StillKeepsItsGalleryPlace()
    {
        var html = LoadFixture("audi_a4_ID11nfyE.html");
        var gallery = OlxListingParser.Parse(html)!.PhotoUrls.Select(url => new Uri(url)).ToList();
        var sut = CreateSut(async request =>
        {
            if (request.RequestUri!.Host != CdnHost)
            {
                return Ok(html);
            }

            // Each photo's bytes carry its gallery position, and the cover answers last.
            var position = gallery.IndexOf(request.RequestUri);
            if (position == 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200));
            }

            return Jpeg([0xFF, 0xD8, (byte)position]);
        });

        var result = await sut.ExtractAsync(
            new Uri("https://www.olx.ua/d/uk/obyavlenie/audi-a4-b7-3-0-tdi-quattro-ID11nfyE.html"), CancellationToken.None);

        var payload = ((Result<MediaPayload, ExtractionError>.Ok)result).Value;
        payload.Items.Select(item => (int)File.ReadAllBytes(item.FilePath)[2]).Should().Equal(Enumerable.Range(0, 10));
        Cleanup(payload);
    }

    [Fact]
    public async Task ExtractAsync_PhotosFail_KeepsTheCaptionForTheTextFallback()
    {
        var html = LoadFixture("solarplay_q2501_ID11svhc.html");
        var sut = CreateSut(request =>
            request.RequestUri!.Host == CdnHost ? new HttpResponseMessage(HttpStatusCode.Forbidden) : Ok(html));

        var result = await sut.ExtractAsync(SolarPlayUrl, CancellationToken.None);

        var payload = result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Ok>().Subject.Value;
        payload.HasMedia.Should().BeFalse();
        payload.Description.Should().StartWith("🏷 SolarPlay Q2501");
    }

    [Fact]
    public async Task ExtractAsync_RemovedAdvert_ReturnsContentUnavailable()
    {
        var sut = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await sut.ExtractAsync(SolarPlayUrl, CancellationToken.None);

        result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Err>()
            .Which.Error.Should().BeOfType<ExtractionError.ContentUnavailable>();
    }

    [Fact]
    public async Task ExtractAsync_ServerError_ReturnsNetworkFailure()
    {
        var sut = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await sut.ExtractAsync(SolarPlayUrl, CancellationToken.None);

        result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Err>()
            .Which.Error.Should().BeOfType<ExtractionError.NetworkFailure>();
    }

    [Fact]
    public async Task ExtractAsync_PageWithoutAdvert_ReturnsAnEmptyPayload()
    {
        var sut = CreateSut(_ => Ok("<html><body>Оголошення не знайдено</body></html>"));

        var result = await sut.ExtractAsync(SolarPlayUrl, CancellationToken.None);

        var payload = result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Ok>().Subject.Value;
        payload.HasMedia.Should().BeFalse();
        payload.Body.Should().BeNull();
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "text/html"),
    };

    private static HttpResponseMessage Jpeg(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes)
        {
            Headers = { ContentType = new MediaTypeHeaderValue("image/jpeg") },
        },
    };

    private static OlxExtractor CreateSut(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        CreateSut(request => Task.FromResult(respond(request)));

    private static OlxExtractor CreateSut(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new StubHttpHandler(respond)));

        var options = Options.Create(new YtDlpOptions
        {
            DownloadDirectory = Path.Combine(Path.GetTempPath(), "lebot-tests-olx"),
            MaxFileSizeMb = 50,
        });

        return new OlxExtractor(factory, options, NullLogger<OlxExtractor>.Instance);
    }

    private static string LoadFixture(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "_fixtures", "olx", fileName));

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

    private sealed class StubHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => respond(request);
    }
}
