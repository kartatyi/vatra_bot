using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.MediaExtraction.YtDlp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using YoutubeDLSharp.Options;

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
    public void BuildOptionSet_Always_MergesIntoMp4()
    {
        // The selector can fall through to a DASH video+audio pair, and yt-dlp would then
        // choose the container itself — an mkv that Telegram attaches as a file rather than
        // playing inline. Pin the merge target so a merged repost stays a playable video.
        var optionSet = CreateSut().BuildOptionSet();

        optionSet.MergeOutputFormat.Should().Be(DownloadMergeFormat.Mp4);
    }

    [Fact]
    public void FormatSelector_Always_FallsBackToMergedDashStreams()
    {
        // YouTube stopped publishing a muxed format for most videos, so a selector made only
        // of "best" variants matches nothing there and yt-dlp exits "No video formats found"
        // on a video that plays fine in a browser. The bv*+ba fallbacks are what keep those
        // posts extractable.
        var selector = YtDlpPlatformExtractor.FormatSelector;

        selector.Should().StartWith("best[ext=mp4]/");
        selector.Should().Contain("bv*[ext=mp4]+ba[ext=m4a]");
        selector.Should().Contain("bv*+ba");
    }
}
