using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.MediaExtraction.YtDlp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.Tests.MediaExtraction.YtDlp;

public class YtDlpPlatformExtractorOptionSetTests
{
    private static YtDlpPlatformExtractor CreateSut()
    {
        var options = Options.Create(new YtDlpOptions
        {
            DownloadDirectory = Path.GetTempPath(),
        });
        return new YtDlpPlatformExtractor(options, NullLogger<YtDlpPlatformExtractor>.Instance);
    }

    [Fact]
    public void BuildOptionSet_Always_RanksH264AboveH265()
    {
        // TikTok's h265 (bytevc1) streams claim acodec=aac but ship without an audio
        // track, so the format sort must steer "best" towards h264 — the variants
        // that actually carry sound. A format filter can't do this: [acodec!=none]
        // trusts the same lying metadata.
        var optionSet = CreateSut().BuildOptionSet();

        optionSet.FormatSort.Should().Be("vcodec:h264");
    }

    [Fact]
    public void BuildOptionSet_TwoRuns_WriteToDifferentFiles()
    {
        // The same link posted in two chats at once is extracted twice, side by side; a shared file name
        // would let the first run delete the second one's download before it is sent.
        var sut = CreateSut();

        var first = sut.BuildOptionSet().Output;
        var second = sut.BuildOptionSet().Output;

        first.Should().NotBe(second);
        first.Should().StartWith(Path.GetTempPath()).And.Contain("[%(id)s]").And.EndWith(".%(ext)s");
    }
}
