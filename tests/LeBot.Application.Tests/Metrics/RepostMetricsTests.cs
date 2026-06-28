using LeBot.Application.Metrics;

namespace LeBot.Application.Tests.Metrics;

public class RepostMetricsTests
{
    private static readonly DateTimeOffset Instant = new(2026, 6, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StartedAt_IsCapturedFromTimeProvider()
    {
        var time = new FakeTimeProvider(Instant);

        var metrics = new RepostMetrics(time);

        metrics.StartedAt.Should().Be(Instant);
    }

    [Fact]
    public void StartedAt_IsAStartSnapshot_NotAffectedByLaterClockMovement()
    {
        var time = new FakeTimeProvider(Instant);
        var metrics = new RepostMetrics(time);

        time.Advance(TimeSpan.FromHours(3));

        // StartedAt anchors the /stats uptime to process start, so it must not drift
        // when the clock advances — only the live read (now - StartedAt) does.
        metrics.StartedAt.Should().Be(Instant);
    }
}
