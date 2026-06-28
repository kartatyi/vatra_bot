using System.Net;
using LeBot.Application.Releases;
using LeBot.Domain.Common;
using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.Releases;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.Tests.Releases;

public class GitHubReleaseSourceTests
{
    private const string ExpectedHex =
        "abc123def4567890abc123def4567890abc123def4567890abc123def4567890";

    [Fact]
    public async Task GetLatestAsync_NotFound_ReturnsNoReleases()
    {
        var source = BuildSource(new StubHandler((_, _) => Respond(HttpStatusCode.NotFound, "{}")));

        var result = await source.GetLatestAsync(CancellationToken.None);

        ErrorOf(result).Should().Be(ReleaseSourceError.NoReleases);
    }

    [Fact]
    public async Task GetLatestAsync_AssetWithDigest_ReturnsReleaseInfo()
    {
        var json = await LoadFixtureAsync("with-digest.json");
        var source = BuildSource(new StubHandler((_, _) => Respond(HttpStatusCode.OK, json)));

        var result = await source.GetLatestAsync(CancellationToken.None);

        var info = ValueOf(result);
        info.Version.Should().Be(new ReleaseVersion(1, 4, 0));
        info.TagName.Should().Be("v1.4.0");
        info.ExpectedSha256.Should().Be(ExpectedHex);
        info.AssetUrl.Should().Be(new Uri("https://example.test/download/v1.4.0/LeBot.Host.exe"));
        info.ReleaseNotes.Should().Be("Adds the self-updater.");
    }

    [Fact]
    public async Task GetLatestAsync_NullDigestWithSha256Asset_ReturnsReleaseInfoFromAsset()
    {
        var json = await LoadFixtureAsync("with-sha256-asset.json");
        var handler = new StubHandler((request, _) =>
            request.RequestUri!.AbsoluteUri.EndsWith(".sha256", StringComparison.Ordinal)
                ? Respond(HttpStatusCode.OK, $"{ExpectedHex}  LeBot.Host.exe\n")
                : Respond(HttpStatusCode.OK, json));
        var source = BuildSource(handler);

        var result = await source.GetLatestAsync(CancellationToken.None);

        var info = ValueOf(result);
        info.ExpectedSha256.Should().Be(ExpectedHex);
        info.Version.Should().Be(new ReleaseVersion(1, 4, 0));
    }

    [Fact]
    public async Task GetLatestAsync_AssetMissing_ReturnsAssetMissing()
    {
        var json = await LoadFixtureAsync("asset-missing.json");
        var source = BuildSource(new StubHandler((_, _) => Respond(HttpStatusCode.OK, json)));

        var result = await source.GetLatestAsync(CancellationToken.None);

        ErrorOf(result).Should().Be(ReleaseSourceError.AssetMissing);
    }

    [Fact]
    public async Task GetLatestAsync_NoChecksumAvailable_ReturnsChecksumMissing()
    {
        var json = await LoadFixtureAsync("no-checksum.json");
        var source = BuildSource(new StubHandler((_, _) => Respond(HttpStatusCode.OK, json)));

        var result = await source.GetLatestAsync(CancellationToken.None);

        ErrorOf(result).Should().Be(ReleaseSourceError.ChecksumMissing);
    }

    [Fact]
    public async Task GetLatestAsync_MalformedTag_ReturnsMalformedResponse()
    {
        var json = await LoadFixtureAsync("malformed-tag.json");
        var source = BuildSource(new StubHandler((_, _) => Respond(HttpStatusCode.OK, json)));

        var result = await source.GetLatestAsync(CancellationToken.None);

        ErrorOf(result).Should().Be(ReleaseSourceError.MalformedResponse);
    }

    private static GitHubReleaseSource BuildSource(StubHandler handler)
    {
        var factory = new StubHttpClientFactory(handler);
        var options = Options.Create(new UpdateOptions
        {
            Repository = "kartatyi/vatra_bot",
            AssetName = "LeBot.Host.exe",
        });
        return new GitHubReleaseSource(factory, options, NullLogger<GitHubReleaseSource>.Instance);
    }

    private static HttpResponseMessage Respond(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body) };

    private static ReleaseInfo ValueOf(Result<ReleaseInfo, ReleaseSourceError> result)
    {
        result.Should().BeOfType<Result<ReleaseInfo, ReleaseSourceError>.Ok>();
        return ((Result<ReleaseInfo, ReleaseSourceError>.Ok)result).Value;
    }

    private static ReleaseSourceError ErrorOf(Result<ReleaseInfo, ReleaseSourceError> result)
    {
        result.Should().BeOfType<Result<ReleaseInfo, ReleaseSourceError>.Err>();
        return ((Result<ReleaseInfo, ReleaseSourceError>.Err)result).Error;
    }

    private static async Task<string> LoadFixtureAsync(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "_fixtures", "github-releases", fileName);
        return await File.ReadAllTextAsync(path);
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request, cancellationToken));
    }
}
