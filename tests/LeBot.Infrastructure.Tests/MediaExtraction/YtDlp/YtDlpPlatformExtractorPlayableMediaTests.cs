using LeBot.Infrastructure.MediaExtraction.YtDlp;
using YoutubeDLSharp.Metadata;

namespace LeBot.Infrastructure.Tests.MediaExtraction.YtDlp;

public class YtDlpPlatformExtractorPlayableMediaTests
{
    [Fact]
    public void HasPlayableMedia_NullFormats_ReturnsFalse()
    {
        var result = YtDlpPlatformExtractor.HasPlayableMedia(null);

        result.Should().BeFalse();
    }

    [Fact]
    public void HasPlayableMedia_EmptyFormats_ReturnsFalse()
    {
        var result = YtDlpPlatformExtractor.HasPlayableMedia([]);

        result.Should().BeFalse();
    }

    [Fact]
    public void HasPlayableMedia_StoryboardOnlyFormats_ReturnsFalse()
    {
        // The shape yt-dlp hands back for a webpage that only embeds a YouTube video reached
        // through the generic extractor: storyboard frames with no real streams. Downloading
        // these fails with "No video formats found", so they must count as no playable media.
        var formats = new[]
        {
            Storyboard("sb2"),
            Storyboard("sb1"),
            Storyboard("sb0"),
        };

        var result = YtDlpPlatformExtractor.HasPlayableMedia(formats);

        result.Should().BeFalse();
    }

    [Fact]
    public void HasPlayableMedia_VideoOnlyFormat_ReturnsTrue()
    {
        var formats = new[]
        {
            Storyboard("sb0"),
            new FormatData { FormatId = "137", VideoCodec = "avc1.640028", AudioCodec = "none" },
        };

        var result = YtDlpPlatformExtractor.HasPlayableMedia(formats);

        result.Should().BeTrue();
    }

    [Fact]
    public void HasPlayableMedia_AudioOnlyFormat_ReturnsTrue()
    {
        var formats = new[]
        {
            new FormatData { FormatId = "251", VideoCodec = "none", AudioCodec = "opus" },
        };

        var result = YtDlpPlatformExtractor.HasPlayableMedia(formats);

        result.Should().BeTrue();
    }

    private static FormatData Storyboard(string id) => new()
    {
        FormatId = id,
        Extension = "mhtml",
        Protocol = "mhtml",
        VideoCodec = "none",
        AudioCodec = "none",
    };
}
