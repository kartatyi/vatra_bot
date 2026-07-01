using System.Net;
using System.Net.Http.Headers;
using LeBot.Domain.Common;
using LeBot.Domain.Media;
using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.MediaExtraction.AutoRia;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.Tests.MediaExtraction.AutoRia;

public class AutoRiaExtractorTests
{
    [Theory]
    [InlineData("https://auto.ria.com/uk/auto_bmw_x5_39837585.html", true)]
    [InlineData("https://auto.ria.com/auto_bmw_x5_39837585.html", true)]
    [InlineData("https://www.auto.ria.com/uk/auto_bmw_x5_39837585.html", true)]
    [InlineData("https://m.auto.ria.com/uk/auto_bmw_x5_39837585.html", true)]
    [InlineData("https://auto.ria.com/uk/newauto_toyota_camry_12345678.html", true)]
    [InlineData("https://auto.ria.com/uk/car/used/", false)]
    [InlineData("https://auto.ria.com/uk/legkovie/", false)]
    [InlineData("https://auto.ria.ua/uk/auto_bmw_x5_39837585.html", false)]
    [InlineData("https://ria.com/uk/auto_bmw_x5_39837585.html", false)]
    [InlineData("https://tiktok.com/auto_bmw_x5_39837585.html", false)]
    public void CanHandle_ScopesToAutoRiaAdvertUrls(string url, bool expected)
    {
        var sut = CreateSut(_ => Ok("<html></html>"));

        sut.CanHandle(new Uri(url)).Should().Be(expected);
    }

    [Fact]
    public async Task ExtractAsync_Advert_RepostsTenPhotosWithSpecCaption()
    {
        var html = LoadFixture("bmw_x5_39837585.html");
        var sut = CreateSut(request =>
            request.RequestUri!.Host == "cdn.riastatic.com" ? JpegBytes() : Ok(html));

        var result = await sut.ExtractAsync(
            new Uri("https://auto.ria.com/uk/auto_bmw_x5_39837585.html"), CancellationToken.None);

        var ok = result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Ok>().Subject;
        ok.Value.Items.Should().HaveCount(10);
        ok.Value.Items.Should().AllSatisfy(i => i.Kind.Should().Be(MediaKind.Photo));
        ok.Value.Description.Should().Contain("🚗 BMW X5 2019");
        ok.Value.Description.Should().Contain("🛣 107 тис. км");
        ok.Value.Description.Should().Contain("💵 53 000 $ · 2 366 450 ₴");
        Cleanup(ok.Value);
    }

    [Fact]
    public async Task ExtractAsync_PhotosFailButPageParses_KeepsCaptionForTextFallback()
    {
        var html = LoadFixture("bmw_x5_39837585.html");
        var sut = CreateSut(request =>
            request.RequestUri!.Host == "cdn.riastatic.com"
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : Ok(html));

        var result = await sut.ExtractAsync(
            new Uri("https://auto.ria.com/uk/auto_bmw_x5_39837585.html"), CancellationToken.None);

        var ok = result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Ok>().Subject;
        ok.Value.HasMedia.Should().BeFalse();
        ok.Value.Description.Should().Contain("BMW X5 2019");
    }

    [Fact]
    public async Task ExtractAsync_NonAdvertPage_ReturnsEmptyPayload()
    {
        var sut = CreateSut(_ => Ok("<html><body>captcha</body></html>"));

        var result = await sut.ExtractAsync(
            new Uri("https://auto.ria.com/uk/auto_ghost_00000000.html"), CancellationToken.None);

        var ok = result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Ok>().Subject;
        ok.Value.HasMedia.Should().BeFalse();
        ok.Value.Description.Should().BeNull();
    }

    [Fact]
    public async Task ExtractAsync_PageHttpError_ReturnsNetworkFailure()
    {
        var sut = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await sut.ExtractAsync(
            new Uri("https://auto.ria.com/uk/auto_bmw_x5_39837585.html"), CancellationToken.None);

        result.Should().BeOfType<Result<MediaPayload, ExtractionError>.Err>()
            .Which.Error.Should().BeOfType<ExtractionError.NetworkFailure>();
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "text/html"),
    };

    private static HttpResponseMessage JpegBytes() => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0]) // tiny JPEG header
        {
            Headers = { ContentType = new MediaTypeHeaderValue("image/jpeg") },
        },
    };

    private static AutoRiaExtractor CreateSut(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new StubHttpHandler(respond)));

        var options = Options.Create(new YtDlpOptions
        {
            DownloadDirectory = Path.Combine(Path.GetTempPath(), "lebot-tests-ria"),
            MaxFileSizeMb = 50,
        });

        return new AutoRiaExtractor(factory, options, NullLogger<AutoRiaExtractor>.Instance);
    }

    private static string LoadFixture(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "_fixtures", "autoria", fileName));

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
