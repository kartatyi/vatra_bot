using LeBot.Infrastructure.MediaProcessing;

namespace LeBot.Infrastructure.Tests.MediaProcessing;

public class CompressionPlanTests
{
    private const long FiftyMb = 50L * 1024 * 1024;

    [Fact]
    public void For_TwoMinuteReelOverCap_StopsAtTheBitrateCeilingInsteadOfFillingTheCap()
    {
        // The reel that started this: 116 s, h264 + AAC, 53 MB against Telegram's 50 MB. The cap
        // would allow ~3100 kbps; the ceiling halves the upload instead.
        var plan = CompressionPlan.For(116, hasAudio: true, FiftyMb);

        plan.Should().Be(new CompressionPlan(VideoKbps: 1500, AudioKbps: 128, MaxLongSide: 1280));
    }

    [Fact]
    public void For_LongVideo_FillsTheCapBelowTheCeiling()
    {
        // 300 s leaves ~1258 kbps in total, under the ceiling, so the cap is the limit.
        var plan = CompressionPlan.For(300, hasAudio: true, FiftyMb);

        plan.Should().Be(new CompressionPlan(VideoKbps: 1130, AudioKbps: 128, MaxLongSide: 1280));
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
        var plan = CompressionPlan.For(300, hasAudio: false, FiftyMb);

        plan.Should().Be(new CompressionPlan(VideoKbps: 1258, AudioKbps: 0, MaxLongSide: 1280));
    }

    [Theory]
    [InlineData(120, 1280)] // at the 1500 kbps ceiling — 720p
    [InlineData(300, 1280)] // ~1130 kbps — still 720p
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
