using LeBot.Domain.Common;
using LeBot.Domain.Media;
using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.MediaExtraction.YtDlp;
using LeBot.Infrastructure.MediaProcessing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.Tests.MediaExtraction.YtDlp;

public sealed class YtDlpPlatformExtractorOversizeTests : IDisposable
{
    private const long CapBytes = 1024 * 1024; // MaxFileSizeMb = 1

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "lebot-oversize-tests", Guid.NewGuid().ToString("N"));

    private readonly IVideoCompressor _compressor = Substitute.For<IVideoCompressor>();

    public YtDlpPlatformExtractorOversizeTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public async Task FitUnderCapAsync_FileWithinCap_PostsItUntouched()
    {
        var clip = WriteFile("clip.mp4", CapBytes);

        var postable = await CreateSut().FitUnderCapAsync(clip, CapBytes, CancellationToken.None);

        postable.Should().Be(clip);
        await _compressor.DidNotReceive()
            .CompressAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FitUnderCapAsync_OversizeVideo_PostsTheCompressedCopyAndDropsTheOriginal()
    {
        var clip = WriteFile("clip.mp4", CapBytes + 1);
        var fitted = WriteFile("clip.fit.mp4", CapBytes / 2);
        _compressor.CompressAsync(clip, CapBytes, Arg.Any<CancellationToken>())
            .Returns(Result<string, ExtractionError>.Success(fitted));

        var postable = await CreateSut().FitUnderCapAsync(clip, CapBytes, CancellationToken.None);

        postable.Should().Be(fitted);
        File.Exists(clip).Should().BeFalse();
    }

    [Fact]
    public async Task FitUnderCapAsync_CompressionFails_DeletesTheDownloadAndFallsBackToText()
    {
        var clip = WriteFile("clip.mp4", CapBytes + 1);
        _compressor.CompressAsync(clip, CapBytes, Arg.Any<CancellationToken>())
            .Returns(Result<string, ExtractionError>.Failure(new ExtractionError.ToolFailure("too long")));

        var postable = await CreateSut().FitUnderCapAsync(clip, CapBytes, CancellationToken.None);

        postable.Should().BeNull();
        File.Exists(clip).Should().BeFalse();
    }

    [Fact]
    public async Task FitUnderCapAsync_CompressionSwitchedOff_NeverRunsTheEncoder()
    {
        var clip = WriteFile("clip.mp4", CapBytes + 1);

        var postable = await CreateSut(compressOversizeVideos: false)
            .FitUnderCapAsync(clip, CapBytes, CancellationToken.None);

        postable.Should().BeNull();
        await _compressor.DidNotReceive()
            .CompressAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FitUnderCapAsync_OversizePhoto_NeverRunsTheEncoder()
    {
        var photo = WriteFile("photo.jpg", CapBytes + 1);

        var postable = await CreateSut().FitUnderCapAsync(photo, CapBytes, CancellationToken.None);

        postable.Should().BeNull();
        await _compressor.DidNotReceive()
            .CompressAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException) { }
    }

    private YtDlpPlatformExtractor CreateSut(bool compressOversizeVideos = true) => new(
        Options.Create(new YtDlpOptions
        {
            DownloadDirectory = _dir,
            MaxFileSizeMb = 1,
            CompressOversizeVideos = compressOversizeVideos,
        }),
        _compressor,
        NullLogger<YtDlpPlatformExtractor>.Instance);

    private string WriteFile(string name, long sizeBytes)
    {
        var path = Path.Combine(_dir, name);
        using var stream = File.Create(path);
        stream.SetLength(sizeBytes);
        return path;
    }
}
