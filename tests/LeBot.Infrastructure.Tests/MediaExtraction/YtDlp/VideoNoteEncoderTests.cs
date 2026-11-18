using LeBot.Infrastructure.MediaExtraction.YtDlp;

namespace LeBot.Infrastructure.Tests.MediaExtraction.YtDlp;

public class VideoNoteEncoderTests
{
    [Fact]
    public void Arguments_CropTheMiddleSquare_AtTheCircleDiameter()
    {
        var args = VideoNoteEncoder.Arguments("in.mp4", "out.mp4", 30);

        args.Should().ContainInOrder("-vf", $"crop='min(iw,ih)':'min(iw,ih)',scale={VideoNoteEncoder.Diameter}:{VideoNoteEncoder.Diameter},setsar=1");
    }

    [Fact]
    public void Arguments_ReadInputFirst_WriteOutputLast()
    {
        var args = VideoNoteEncoder.Arguments("in.mp4", "out.mp4", 30);

        args.Should().ContainInOrder("-i", "in.mp4");
        args[^1].Should().Be("out.mp4");
    }

    [Theory]
    [InlineData(30, "30")]
    [InlineData(90, "60")]
    [InlineData(0, "1")]
    public void Arguments_Length_StaysWithinTelegramsMinute(int seconds, string expected)
    {
        var args = VideoNoteEncoder.Arguments("in.mp4", "out.mp4", seconds);

        args.Should().ContainInOrder("-t", expected);
    }

    [Fact]
    public void Arguments_EncodeWhatEveryTelegramClientPlays()
    {
        var args = VideoNoteEncoder.Arguments("in.mp4", "out.mp4", 30);

        args.Should().ContainInOrder("-c:v", "libx264");
        args.Should().ContainInOrder("-c:a", "aac");
        args.Should().ContainInOrder("-movflags", "+faststart");
    }
}
