using LeBot.Infrastructure.MediaExtraction.YtDlp;
using Newtonsoft.Json;
using YoutubeDLSharp.Metadata;

namespace LeBot.Infrastructure.Tests.MediaExtraction.YtDlp;

public class YtDlpPlatformExtractorSoleEntryTests
{
    [Fact]
    public void UnwrapSoleEntry_OneEntryPlaylist_ReturnsTheEntryWithItsFormats()
    {
        // A Steam store page as yt-dlp hands it back: a playlist wrapping one trailer. The
        // playlist node carries no formats of its own, so reading it made a playable video
        // look like a text-only post.
        var info = LoadFixture("steam-single-entry-playlist.json");

        var video = YtDlpPlatformExtractor.UnwrapSoleEntry(info);

        video.ID.Should().Be("3834090-1");
        video.Formats.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void UnwrapSoleEntry_PlainVideo_ReturnsItself()
    {
        var info = new VideoData
        {
            ID = "7301234567890",
            Formats = [new FormatData { FormatId = "h264_540p", VideoCodec = "h264", AudioCodec = "aac" }],
        };

        var video = YtDlpPlatformExtractor.UnwrapSoleEntry(info);

        video.Should().BeSameAs(info);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void UnwrapSoleEntry_PlaylistWithoutExactlyOneEntry_ReturnsThePlaylist(int entryCount)
    {
        var info = new VideoData
        {
            ID = "carousel",
            Entries = [.. Enumerable.Range(1, entryCount).Select(i => new VideoData { ID = $"carousel-{i}" })],
        };

        var video = YtDlpPlatformExtractor.UnwrapSoleEntry(info);

        video.Should().BeSameAs(info);
    }

    private static VideoData LoadFixture(string fileName)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "_fixtures", "ytdlp", fileName));

        // Deserialized the way YoutubeDLSharp reads yt-dlp's JSON, so the test sees the same
        // shape the extractor does — including the playlist node's null Formats.
        return JsonConvert.DeserializeObject<VideoData>(json)
            ?? throw new InvalidOperationException($"fixture {fileName} deserialized to null");
    }
}
