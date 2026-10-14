using LeBot.Domain.Common;
using LeBot.Domain.Media;
using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.MediaProcessing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.Tests.MediaProcessing;

/// <summary>
/// The GPU-first, CPU-always order, driven through a fake <see cref="ToolRunner"/>: each encoder is told
/// to succeed, fail, hang or overshoot, and the compressor must still hand back a file that fits.
/// </summary>
public sealed class FfmpegVideoCompressorFallbackTests : IDisposable
{
    private const long CapBytes = 50L * 1024 * 1024;
    private const string ReelProbe =
        """{ "streams": [ { "codec_type": "video" }, { "codec_type": "audio" } ], "format": { "duration": "116.0" } }""";

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "lebot-compressor-tests", Guid.NewGuid().ToString("N"));

    private readonly FakeTimeProvider _time = new();
    private readonly List<string> _encodersRun = [];
    private readonly List<int> _videoKbpsRun = [];
    private readonly Dictionary<string, Func<string, CancellationToken, Task<ToolRun>>> _encoders = [];
    private string _probe = ReelProbe;

    public FfmpegVideoCompressorFallbackTests() => Directory.CreateDirectory(_dir);

    private string Source => Path.Combine(_dir, "clip.mp4");

    private string Output => Path.Combine(_dir, "clip.fit.mp4");

    [Fact]
    public async Task CompressAsync_GpuEncodes_NeverTouchesTheCpu()
    {
        _encoders["h264_nvenc"] = Writes(CapBytes / 2);
        _encoders["libx264"] = Writes(CapBytes / 2);

        var result = await CreateSut().CompressAsync(Source, CapBytes, CancellationToken.None);

        result.Should().Be(Result<string, ExtractionError>.Success(Output));
        _encodersRun.Should().Equal("h264_nvenc");
    }

    [Fact]
    public async Task CompressAsync_GpuRefuses_CompressesOnTheCpu()
    {
        _encoders["h264_nvenc"] = Fails("[h264_nvenc] Driver does not support the required nvenc API version");
        _encoders["libx264"] = Writes(CapBytes / 2);

        var result = await CreateSut().CompressAsync(Source, CapBytes, CancellationToken.None);

        result.Should().Be(Result<string, ExtractionError>.Success(Output));
        _encodersRun.Should().Equal("h264_nvenc", "libx264");
    }

    [Fact]
    public async Task CompressAsync_GpuOvershootsTwice_CompressesOnTheCpu()
    {
        _encoders["h264_nvenc"] = Writes(CapBytes + 1);
        _encoders["libx264"] = Writes(CapBytes / 2);

        var result = await CreateSut().CompressAsync(Source, CapBytes, CancellationToken.None);

        result.Should().Be(Result<string, ExtractionError>.Success(Output));
        _encodersRun.Should().Equal("h264_nvenc", "h264_nvenc", "libx264");
        new FileInfo(Output).Length.Should().Be(CapBytes / 2);
    }

    [Fact]
    public async Task CompressAsync_CpuOvershoots_RetriesAtALowerBitrateUntilItFits()
    {
        _encoders["h264_nvenc"] = Fails("no NVIDIA card");
        _encoders["libx264"] = InTurn(Writes(CapBytes + CapBytes / 10), Writes(CapBytes / 2));

        var result = await CreateSut().CompressAsync(Source, CapBytes, CancellationToken.None);

        result.Should().Be(Result<string, ExtractionError>.Success(Output));
        _encodersRun.Should().Equal("h264_nvenc", "libx264", "libx264");
        _videoKbpsRun[2].Should().BeLessThan(_videoKbpsRun[1]);
    }

    [Fact]
    public async Task CompressAsync_CpuOvershootsTwice_GivesUpRatherThanPostAnOversizeFile()
    {
        _encoders["h264_nvenc"] = Fails("no NVIDIA card");
        _encoders["libx264"] = Writes(CapBytes + 1);

        var result = await CreateSut().CompressAsync(Source, CapBytes, CancellationToken.None);

        result.Should().BeOfType<Result<string, ExtractionError>.Err>()
            .Which.Error.Should().BeOfType<ExtractionError.ToolFailure>()
            .Which.Detail.Should().StartWith("libx264 still came out at 50MB");
        _encodersRun.Should().Equal("h264_nvenc", "libx264", "libx264");
        File.Exists(Output).Should().BeFalse();
    }

    [Fact]
    public async Task CompressAsync_GpuHangs_CompressesOnTheCpuOnceItsBudgetRunsOut()
    {
        var gpuCancelled = false;
        _encoders["h264_nvenc"] = async (_, token) =>
        {
            try
            {
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException)
            {
                gpuCancelled = true;
                throw;
            }

            return new ToolRun(0, string.Empty, string.Empty);
        };
        _encoders["libx264"] = Writes(CapBytes / 2);

        var compressing = CreateSut().CompressAsync(Source, CapBytes, CancellationToken.None);
        _time.Advance(FfmpegVideoCompressor.HardwareEncoderTimeout);
        var result = await compressing;

        result.Should().Be(Result<string, ExtractionError>.Success(Output));
        gpuCancelled.Should().BeTrue("a wedged GPU encode must be killed, not left running");
        _encodersRun.Should().Equal("h264_nvenc", "libx264");
    }

    [Fact]
    public async Task CompressAsync_EveryEncoderFails_ReportsTheLastOne()
    {
        _encoders["h264_nvenc"] = Fails("no NVIDIA card");
        _encoders["libx264"] = Fails("Conversion failed!");

        var result = await CreateSut().CompressAsync(Source, CapBytes, CancellationToken.None);

        result.Should().BeOfType<Result<string, ExtractionError>.Err>()
            .Which.Error.Should().BeOfType<ExtractionError.ToolFailure>()
            .Which.Detail.Should().Be("libx264 failed: Conversion failed!");
        File.Exists(Output).Should().BeFalse();
    }

    [Fact]
    public async Task CompressAsync_TooLongForTheCap_RunsNoEncoder()
    {
        _probe = """{ "streams": [], "format": { "duration": "1200.0" } }""";

        var result = await CreateSut().CompressAsync(Source, CapBytes, CancellationToken.None);

        result.Should().BeOfType<Result<string, ExtractionError>.Err>();
        _encodersRun.Should().BeEmpty();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException) { }
    }

    private FfmpegVideoCompressor CreateSut() => new(
        Options.Create(new YtDlpOptions { FfmpegPath = "ffmpeg" }),
        _time,
        RunAsync,
        NullLogger<FfmpegVideoCompressor>.Instance);

    private Task<ToolRun> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
    {
        if (executable == "ffprobe")
        {
            return Task.FromResult(new ToolRun(0, _probe, string.Empty));
        }

        var encoder = arguments[arguments.ToList().IndexOf("-c:v") + 1];
        _encodersRun.Add(encoder);
        _videoKbpsRun.Add(int.Parse(arguments[arguments.ToList().IndexOf("-b:v") + 1].TrimEnd('k')));
        return _encoders[encoder](arguments[^1], token);
    }

    private static Func<string, CancellationToken, Task<ToolRun>> Writes(long sizeBytes) => (outputPath, _) =>
    {
        using (var stream = File.Create(outputPath))
        {
            stream.SetLength(sizeBytes);
        }

        return Task.FromResult(new ToolRun(0, string.Empty, string.Empty));
    };

    private static Func<string, CancellationToken, Task<ToolRun>> InTurn(
        params Func<string, CancellationToken, Task<ToolRun>>[] runs)
    {
        var next = 0;
        return (outputPath, token) => runs[Math.Min(next++, runs.Length - 1)](outputPath, token);
    }

    private static Func<string, CancellationToken, Task<ToolRun>> Fails(string error) =>
        (_, _) => Task.FromResult(new ToolRun(1, string.Empty, error + "\nError while opening encoder"));
}
