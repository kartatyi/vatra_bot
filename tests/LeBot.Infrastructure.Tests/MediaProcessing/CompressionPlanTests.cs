using LeBot.Infrastructure.MediaProcessing;

namespace LeBot.Infrastructure.Tests.MediaProcessing;

public class CompressionPlanTests
{
    private const long FiftyMb = 50L * 1024 * 1024;

    [Fact]
    public void For_TwoMinuteReelOverCap_FillsTheCap()
    {
        // The reel that started this: 116 s, h264 + AAC, 53 MB against Telegram's 50 MB. Anything less
        // than the cap allows showed: 22 MB scored VMAF 69 against the source, 44 MB scored 91–96.
        var plan = CompressionPlan.For(116, hasAudio: true, FiftyMb);

        plan.Should().Be(new CompressionPlan(VideoKbps: 3322, AudioKbps: 128, MaxLongSide: 1920));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(30)]
    [InlineData(116)]
    [InlineData(300)]
    [InlineData(700)]
    public void For_AnyFeasibleLength_FitsEvenWhenTheEncoderSpendsItsWholeBuffer(double durationSeconds)
    {
        var plan = CompressionPlan.For(durationSeconds, hasAudio: true, FiftyMb);

        plan.Should().NotBeNull();
        var worstVideoBits = plan!.VideoKbps * 1000.0 * (durationSeconds + CompressionPlan.BufferSeconds); // ! — asserted non-null on the line above
        var audioBits = plan.AudioKbps * 1000.0 * durationSeconds;
        ((worstVideoBits + audioBits) / 8).Should().BeLessThanOrEqualTo(FiftyMb * CompressionPlan.StreamShare);
    }

    [Fact]
    public void For_SilentVideo_GivesTheAudioShareToVideo()
    {
        var plan = CompressionPlan.For(116, hasAudio: false, FiftyMb);

        plan.Should().Be(new CompressionPlan(VideoKbps: 3447, AudioKbps: 0, MaxLongSide: 1920));
    }

    [Theory]
    [InlineData(116, 1920)] // ~3300 kbps — full HD holds up
    [InlineData(300, 1280)] // ~1200 kbps — 720p
    [InlineData(500, 854)]  // ~680 kbps — 480p, or the picture turns to blocks
    public void For_FallingBitrate_ShrinksThePicture(double durationSeconds, int expectedLongSide)
    {
        var plan = CompressionPlan.For(durationSeconds, hasAudio: true, FiftyMb);

        plan!.MaxLongSide.Should().Be(expectedLongSide); // ! — every length here is feasible
    }

    [Fact]
    public void For_VideoTooLongForAWatchableBitrate_ReturnsNull()
    {
        // Twenty minutes in 50 MB leaves about 200 kbps for the picture.
        var plan = CompressionPlan.For(20 * 60, hasAudio: true, FiftyMb);

        plan.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void For_NoUsableDuration_ReturnsNull(double durationSeconds)
    {
        var plan = CompressionPlan.For(durationSeconds, hasAudio: true, FiftyMb);

        plan.Should().BeNull();
    }
}
