using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.MediaExtraction.Instagram;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.Tests.MediaExtraction.Instagram;

public class YtDlpCookieJarReaderTests
{
    [Fact]
    public async Task ReadAsync_BinaryMissing_ReturnsNullInsteadOfThrowing()
    {
        // Pointing at a non-existent binary makes Process.Start throw; the reader must swallow it and
        // surface null so the provider can fall back to a stale session rather than crash the update.
        var options = Options.Create(new YtDlpOptions
        {
            BinaryPath = "no-such-yt-dlp-binary-zzz.exe",
        });
        var sut = new YtDlpCookieJarReader(options, NullLogger<YtDlpCookieJarReader>.Instance);

        var result = await sut.ReadAsync("firefox", CancellationToken.None);

        result.Should().BeNull();
    }
}
