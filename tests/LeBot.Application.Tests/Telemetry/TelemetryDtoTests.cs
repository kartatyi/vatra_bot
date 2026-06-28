using LeBot.Application.Telemetry;

namespace LeBot.Application.Tests.Telemetry;

/// <summary>
/// The read-side DTOs are mostly data, but their rate properties carry the one piece of logic worth
/// pinning down: the success/failure definitions and the divide-by-zero guard for an empty window.
/// </summary>
public class TelemetryDtoTests
{
    [Fact]
    public void RepostStatsSnapshot_SuccessRate_CountsMediaAndTextOverTotal()
    {
        var snapshot = new RepostStatsSnapshot(
            TotalProcessed: 10, MediaReposts: 4, TextFallbacks: 1,
            Failures: 3, NothingExtracted: 1, NoExtractor: 1,
            DistinctChats: 2, FirstEventAt: null, LastEventAt: null);

        snapshot.Successes.Should().Be(5);
        snapshot.SuccessRate.Should().Be(0.5);
    }

    [Fact]
    public void RepostStatsSnapshot_Empty_HasZeroRateNotDivideByZero()
    {
        RepostStatsSnapshot.Empty.SuccessRate.Should().Be(0d);
        RepostStatsSnapshot.Empty.TotalProcessed.Should().Be(0);
    }

    [Fact]
    public void HostStat_FailureRate_IsFailuresOverTotal()
    {
        new HostStat("tiktok.com", Total: 8, Failures: 2).FailureRate.Should().Be(0.25);
    }

    [Fact]
    public void HostStat_FailureRate_EmptyHost_IsZero()
    {
        new HostStat("tiktok.com", Total: 0, Failures: 0).FailureRate.Should().Be(0d);
    }

    [Fact]
    public void ExtractorStat_SuccessRate_IsSuccessesOverTotal()
    {
        new ExtractorStat("YtDlpPlatformExtractor", Total: 4, Successes: 3, Failures: 1)
            .SuccessRate.Should().Be(0.75);
    }

    [Fact]
    public void ExtractorStat_SuccessRate_EmptyExtractor_IsZero()
    {
        new ExtractorStat("YtDlpPlatformExtractor", Total: 0, Successes: 0, Failures: 0)
            .SuccessRate.Should().Be(0d);
    }

    [Fact]
    public void LatencySummary_Empty_IsAllZero()
    {
        LatencySummary.Empty.SampleCount.Should().Be(0);
        LatencySummary.Empty.AverageMs.Should().Be(0d);
        LatencySummary.Empty.P95Ms.Should().Be(0L);
    }
}
