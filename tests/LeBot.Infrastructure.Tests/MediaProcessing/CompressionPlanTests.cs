using LeBot.Infrastructure.MediaProcessing;

namespace LeBot.Infrastructure.Tests.MediaProcessing;

public class CompressionPlanTests
{
    private const long FiftyMb = 50L * 1024 * 1024;

    [Fact]
    public void For_TwoMinuteReelOverCap_SpendsTheCapOnVideoAfterAudio()
    {
        // The reel that started this: 116 s, h264 + AAC, 53 MB against Telegram's 50 MB.
        var plan = CompressionPlan.For(116, hasAudio: true, FiftyMb);

        plan.Should().Be(new CompressionPlan(VideoKbps: 3126, AudioKbps: 128, MaxLongSide: 1920));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(116)]
    [InlineData(300)]
    [InlineData(700)]
    public void For_AnyFeasibleLength_AimsInsideTheCap(double durationSeconds)
    {
        var plan = CompressionPlan.For(durationSeconds, hasAudio: true, FiftyMb);

        plan.Should().NotBeNull();
        var plannedBytes = (plan!.VideoKbps + plan.AudioKbps) * 1000.0 / 8 * durationSeconds; // ! — asserted non-null on the line above
        plannedBytes.Should().BeLessThanOrEqualTo(FiftyMb * CompressionPlan.CapShare);
    }

    [Fact]
    public void For_SilentVideo_GivesTheAudioShareToVideo()
    {
        var plan = CompressionPlan.For(116, hasAudio: false, FiftyMb);

        plan.Should().Be(new CompressionPlan(VideoKbps: 3254, AudioKbps: 0, MaxLongSide: 1920));
    }

    [Theory]
    [InlineData(120, 1920)] // ~3000 kbps — full HD holds up
    [InlineData(240, 1280)] // ~1450 kbps — 720p
    [InlineData(500, 854)]  // ~630 kbps — 480p, or the picture turns to blocks
    public void For_FallingBitrate_ShrinksThePicture(double durationSeconds, int expectedLongSide)
    {
        var plan = CompressionPlan.For(durationSeconds, hasAudio: true, FiftyMb);

        plan!.MaxLongSide.Should().Be(expectedLongSide); // ! — every length here is feasible
    }

    [Fact]
    public void For_VideoTooLongForAWatchableBitrate_ReturnsNull()
    {
        // Twenty minutes in 50 MB leaves under 200 kbps for the picture.
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
