using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.MediaExtraction.Instagram;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.Tests.MediaExtraction.Instagram;

public class YtDlpCookieProviderTests
{
    private static readonly DateTimeOffset Instant = new(2026, 6, 28, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] ValidJar =
    [
        "# Netscape HTTP Cookie File",
        ".instagram.com\tTRUE\t/\tTRUE\t1790000000\tcsrftoken\tCSRF123",
        ".instagram.com\tTRUE\t/\tTRUE\t1790000000\tds_user_id\t5610538717",
        ".instagram.com\tTRUE\t/\tTRUE\t1790000000\tsessionid\t5610538717%3Aabc%3A27",
    ];

    [Fact]
    public void ParseJar_RealShape_ExtractsSessionCookies()
    {
        var cookies = YtDlpCookieProvider.ParseJar(ValidJar);

        cookies.Should().NotBeNull();
        cookies!.SessionId.Should().Be("5610538717%3Aabc%3A27");
        cookies.DsUserId.Should().Be("5610538717");
        cookies.CsrfToken.Should().Be("CSRF123");
        cookies.ToHeaderValue().Should().Contain("sessionid=5610538717%3Aabc%3A27");
    }

    [Fact]
    public void ParseJar_HttpOnlyPrefixedSessionId_IsStillRead()
    {
        // Some exporters mark HttpOnly cookies with a "#HttpOnly_" prefix instead of a plain row.
        var jar = new[]
        {
            "# Netscape HTTP Cookie File",
            "#HttpOnly_.instagram.com\tTRUE\t/\tTRUE\t1790000000\tsessionid\tSECRET",
        };

        YtDlpCookieProvider.ParseJar(jar)!.SessionId.Should().Be("SECRET");
    }

    [Fact]
    public void ParseJar_NoInstagramSessionId_ReturnsNull()
    {
        var jar = new[]
        {
            ".instagram.com\tTRUE\t/\tTRUE\t1790000000\tcsrftoken\tCSRF123",
            ".example.com\tTRUE\t/\tTRUE\t1790000000\tsessionid\tNOT_INSTAGRAM",
        };

        YtDlpCookieProvider.ParseJar(jar).Should().BeNull();
    }

    [Theory]
    [InlineData("notinstagram.com")]
    [InlineData("instagram.com.evil.example")]
    [InlineData(".instagram.com.evil.example")]
    public void ParseJar_LookAlikeDomain_IsNotHarvested(string domain)
    {
        var jar = new[] { $"{domain}\tTRUE\t/\tTRUE\t1790000000\tsessionid\tFOREIGN" };

        YtDlpCookieProvider.ParseJar(jar).Should().BeNull();
    }

    [Fact]
    public void ParseJar_InstagramSubdomain_IsAccepted()
    {
        var jar = new[] { "i.instagram.com\tTRUE\t/\tTRUE\t1790000000\tsessionid\tOK" };

        YtDlpCookieProvider.ParseJar(jar)!.SessionId.Should().Be("OK");
    }

    [Fact]
    public async Task GetAsync_NoBrowserConfigured_ReturnsNullWithoutReadingJar()
    {
        var reader = Substitute.For<IBrowserCookieJarReader>();
        var sut = CreateSut(reader, browser: null, out _);

        var result = await sut.GetAsync(forceRefresh: false, CancellationToken.None);

        result.Should().BeNull();
        await reader.DidNotReceiveWithAnyArgs().ReadAsync(default!, default);
    }

    [Fact]
    public async Task GetAsync_ValidJar_ReturnsSessionCookies()
    {
        var reader = ReaderReturning(ValidJar);
        var sut = CreateSut(reader, browser: "firefox", out _);

        var result = await sut.GetAsync(forceRefresh: false, CancellationToken.None);

        result.Should().NotBeNull();
        result!.SessionId.Should().Be("5610538717%3Aabc%3A27");
    }

    [Fact]
    public async Task GetAsync_SecondCallWithinTtl_ServesFromCacheWithoutReReading()
    {
        var reader = ReaderReturning(ValidJar);
        var sut = CreateSut(reader, browser: "firefox", out var time);

        await sut.GetAsync(forceRefresh: false, CancellationToken.None);
        time.Advance(TimeSpan.FromHours(1));
        await sut.GetAsync(forceRefresh: false, CancellationToken.None);

        await reader.Received(1).ReadAsync("firefox", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAsync_ForceRefresh_BypassesCache()
    {
        var reader = ReaderReturning(ValidJar);
        var sut = CreateSut(reader, browser: "firefox", out _);

        await sut.GetAsync(forceRefresh: false, CancellationToken.None);
        await sut.GetAsync(forceRefresh: true, CancellationToken.None);

        await reader.Received(2).ReadAsync("firefox", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAsync_RefreshFails_FallsBackToStaleSession()
    {
        var reader = Substitute.For<IBrowserCookieJarReader>();
        reader.ReadAsync("firefox", Arg.Any<CancellationToken>())
            .Returns(
                _ => Task.FromResult<IReadOnlyList<string>?>(ValidJar),
                _ => Task.FromResult<IReadOnlyList<string>?>(null));
        var sut = CreateSut(reader, browser: "firefox", out _);

        var first = await sut.GetAsync(forceRefresh: false, CancellationToken.None);
        var second = await sut.GetAsync(forceRefresh: true, CancellationToken.None);

        first.Should().NotBeNull();
        second.Should().BeSameAs(first);
    }

    private static IBrowserCookieJarReader ReaderReturning(IReadOnlyList<string> jar)
    {
        var reader = Substitute.For<IBrowserCookieJarReader>();
        reader.ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>?>(jar));
        return reader;
    }

    private static YtDlpCookieProvider CreateSut(
        IBrowserCookieJarReader reader,
        string? browser,
        out FakeTimeProvider time)
    {
        time = new FakeTimeProvider(Instant);
        var options = Options.Create(new YtDlpOptions { CookiesFromBrowser = browser });
        return new YtDlpCookieProvider(reader, options, time, NullLogger<YtDlpCookieProvider>.Instance);
    }
}
