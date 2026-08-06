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
}
