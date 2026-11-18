using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.MediaExtraction.YtDlp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using YoutubeDLSharp.Metadata;

namespace LeBot.Infrastructure.Tests.MediaExtraction.YtDlp;

/// <summary>
/// A live stream never ends, so it must never reach the ordinary download. That is how one link froze the
/// bot for every chat for 2.5 hours.
/// </summary>
public class YtDlpPlatformExtractorLiveTests
{
    [Theory]
    [InlineData(true, LiveStatus.None)]
    [InlineData(null, LiveStatus.IsLive)]
    public void IsLiveBroadcast_OnAirNow_IsTrue(bool? isLive, LiveStatus status)
    {
        var info = new VideoData { IsLive = isLive, LiveStatus = status };

        YtDlpPlatformExtractor.IsLiveBroadcast(info).Should().BeTrue();
    }

    [Theory]
    [InlineData(LiveStatus.WasLive)]
    [InlineData(LiveStatus.IsUpcoming)]
    [InlineData(LiveStatus.NotLive)]
    [InlineData(LiveStatus.None)]
    public void IsLiveBroadcast_NotOnAirNow_IsFalse(LiveStatus status)
    {
        var info = new VideoData { IsLive = false, LiveStatus = status };

        YtDlpPlatformExtractor.IsLiveBroadcast(info).Should().BeFalse();
    }

    [Fact]
    public void BuildLiveOptionSet_StopsFfmpegAfterTheClip()
    {
        var flags = CreateSut().BuildLiveOptionSet(30).GetOptionFlags().ToList();

        flags.Should().Contain("--downloader \"ffmpeg\"");
        flags.Should().Contain("--downloader-args \"ffmpeg_o:-t 30\"");
    }

    [Fact]
    public void BuildLiveOptionSet_KeepsTheUsualOptions()
    {
        var options = CreateSut().BuildLiveOptionSet(30);

        options.FormatSort.Should().Be("vcodec:h264");
        options.GetOptionFlags().Should().Contain("--restrict-filenames");
    }

    [Theory]
    [InlineData("🔴 GTA 6 LAUNCH (PS5 Pro 4K 60FPS) 2026-11-18 16:12", "🔴 GTA 6 LAUNCH (PS5 Pro 4K 60FPS)")]
    [InlineData("Morning news", "Morning news")]
    [InlineData("Recap of 2026-11-18 16:12 match", "Recap of 2026-11-18 16:12 match")]
    [InlineData(null, null)]
    public void LiveTitle_DropsTheTimestampYtDlpAppends(string? title, string? expected)
    {
        YtDlpPlatformExtractor.LiveTitle(title).Should().Be(expected);
    }

    [Fact]
    public void LiveClipFormat_CapsTheRecordingAt480p()
    {
        YtDlpPlatformExtractor.LiveClipFormat.Should().StartWith("bv*[height<=480]+ba");
    }

    private static YtDlpPlatformExtractor CreateSut() =>
        new(
            Options.Create(new YtDlpOptions { DownloadDirectory = Path.GetTempPath() }),
            TimeProvider.System,
            NullLogger<YtDlpPlatformExtractor>.Instance);
}
