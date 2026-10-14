using LeBot.Domain.Common;
using LeBot.Domain.Media;
using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.MediaProcessing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.Tests.MediaProcessing;

public class FfmpegVideoCompressorTests
{
    // Verbatim ffprobe output for the 116 s Instagram reel that came out at 53 MB.
    private const string ReelProbe = """
        {
            "programs": [

            ],
            "stream_groups": [

            ],
            "streams": [
                {
                    "codec_type": "video"
                },
                {
                    "codec_type": "audio"
                }
            ],
            "format": {
                "duration": "116.004036"
            }
        }
        """;

    [Fact]
    public void ParseProbe_FfprobeOutput_ReadsDurationAndSoundTrack()
    {
        var media = FfmpegVideoCompressor.ParseProbe(ReelProbe);

        media.Should().Be((116.004036, true));
    }

    [Fact]
    public void ParseProbe_NoAudioStream_ReportsSilentVideo()
    {
        const string json = """{ "streams": [ { "codec_type": "video" } ], "format": { "duration": "9.5" } }""";

        var media = FfmpegVideoCompressor.ParseProbe(json);

        media.Should().Be((9.5, false));
    }

    [Theory]
    [InlineData("""{ "streams": [], "format": {} }""")]
    [InlineData("""{ "streams": [], "format": { "duration": "N/A" } }""")]
    [InlineData("""{ "streams": [], "format": { "duration": 12.5 } }""")]
    [InlineData("not json")]
    public void ParseProbe_NoReadableDuration_ReturnsNull(string json)
    {
        var media = FfmpegVideoCompressor.ParseProbe(json);

        media.Should().BeNull();
    }

    [Fact]
    public void SiblingTool_ToolInAFolder_SharesItsFolderAndExtension()
    {
        var ytDlp = Path.Combine("tools", "yt-dlp", "yt-dlp.exe");

        var ffmpeg = FfmpegVideoCompressor.SiblingTool(ytDlp, "ffmpeg");

        ffmpeg.Should().Be(Path.Combine("tools", "yt-dlp", "ffmpeg.exe"));
    }

    [Fact]
    public void SiblingTool_BareName_StaysBareForAPathLookup()
    {
        var ffprobe = FfmpegVideoCompressor.SiblingTool("ffmpeg", "ffprobe");

        ffprobe.Should().Be("ffprobe");
    }

    [Fact]
    public void ResolveFfmpegPath_FfmpegPathUnset_UsesTheOneBesideYtDlp()
    {
        var toolDir = Path.Combine(Path.GetTempPath(), "lebot-tools");
        var options = new YtDlpOptions { BinaryPath = Path.Combine(toolDir, "yt-dlp.exe") };

        var ffmpeg = FfmpegVideoCompressor.ResolveFfmpegPath(options);

        ffmpeg.Should().Be(Path.Combine(toolDir, "ffmpeg.exe"));
    }

    [Fact]
    public void ResolveFfmpegPath_FfmpegPathSet_Wins()
    {
        var configured = Path.Combine(Path.GetTempPath(), "elsewhere", "ffmpeg.exe");
        var options = new YtDlpOptions { FfmpegPath = configured };

        var ffmpeg = FfmpegVideoCompressor.ResolveFfmpegPath(options);

        ffmpeg.Should().Be(configured);
    }

    [Fact]
    public void BuildEncodeArguments_WithAudio_EncodesH264AndAacAtThePlannedRates()
    {
        var plan = new CompressionPlan(VideoKbps: 3126, AudioKbps: 128, MaxLongSide: 1920);

        var arguments = FfmpegVideoCompressor.BuildEncodeArguments("in.mp4", "out.mp4", plan, "libx264");

        arguments.Should().ContainInConsecutiveOrder("-c:v", "libx264", "-preset", "fast");
        arguments.Should().ContainInConsecutiveOrder("-pix_fmt", "yuv420p");
        arguments.Should().ContainInConsecutiveOrder("-b:v", "3126k");
        arguments.Should().ContainInConsecutiveOrder("-c:a", "aac", "-b:a", "128k");
        arguments.Should().ContainInConsecutiveOrder("-i", "in.mp4");
        arguments[^1].Should().Be("out.mp4");
    }

    [Fact]
    public void BuildEncodeArguments_Nvenc_EncodesOnTheGpuAtTheSameRate()
    {
        var plan = new CompressionPlan(VideoKbps: 1500, AudioKbps: 128, MaxLongSide: 1280);

        var arguments = FfmpegVideoCompressor.BuildEncodeArguments("in.mp4", "out.mp4", plan, "h264_nvenc");

        arguments.Should().ContainInConsecutiveOrder("-c:v", "h264_nvenc", "-preset", "p5", "-tune", "hq", "-rc", "vbr");
        arguments.Should().ContainInConsecutiveOrder("-b:v", "1500k", "-maxrate", "1500k");
        arguments.Should().ContainInConsecutiveOrder("-pix_fmt", "yuv420p");
        arguments.Should().NotContain("libx264");
    }

    [Fact]
    public void VideoEncoders_GpuFirst_CpuLastBecauseEveryFfmpegHasIt()
    {
        FfmpegVideoCompressor.VideoEncoders.Should().Equal("h264_nvenc", "libx264");
    }

    [Fact]
    public void BuildEncodeArguments_UnknownEncoder_Throws()
    {
        var plan = new CompressionPlan(VideoKbps: 1500, AudioKbps: 128, MaxLongSide: 1280);

        var build = () => FfmpegVideoCompressor.BuildEncodeArguments("in.mp4", "out.mp4", plan, "h265_magic");

        build.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void BuildEncodeArguments_SilentPlan_DropsTheAudioStream()
    {
        var plan = new CompressionPlan(VideoKbps: 3254, AudioKbps: 0, MaxLongSide: 1920);

        var arguments = FfmpegVideoCompressor.BuildEncodeArguments("in.mp4", "out.mp4", plan, "libx264");

        arguments.Should().Contain("-an");
        arguments.Should().NotContain("-c:a");
    }

    [Fact]
    public void BuildEncodeArguments_ScaleFilter_CapsTheLongSideWithoutUpscaling()
    {
        var plan = new CompressionPlan(VideoKbps: 600, AudioKbps: 128, MaxLongSide: 854);

        var arguments = FfmpegVideoCompressor.BuildEncodeArguments("in.mp4", "out.mp4", plan, "libx264");

        arguments.Should().ContainInConsecutiveOrder(
            "-vf",
            "scale='min(iw,854)':'min(ih,854)':force_original_aspect_ratio=decrease:force_divisible_by=2");
    }

    [Fact]
    public async Task CompressAsync_FfmpegMissing_FailsInsteadOfThrowing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "lebot-no-such-dir", "ffmpeg.exe");
        var sut = new FfmpegVideoCompressor(
            Options.Create(new YtDlpOptions { FfmpegPath = missing }),
            TimeProvider.System,
            NullLogger<FfmpegVideoCompressor>.Instance);

        var result = await sut.CompressAsync(
            Path.Combine(Path.GetTempPath(), "clip.mp4"), 50L * 1024 * 1024, CancellationToken.None);

        result.Should().BeOfType<Result<string, ExtractionError>.Err>()
            .Which.Error.Should().BeOfType<ExtractionError.ToolFailure>()
            .Which.Detail.Should().Contain("could not start");
    }
}
