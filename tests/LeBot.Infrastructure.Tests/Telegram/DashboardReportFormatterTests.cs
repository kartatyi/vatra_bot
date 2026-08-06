using LeBot.Application.Metrics;
using LeBot.Application.Telemetry;
using LeBot.Infrastructure.Telegram;

namespace LeBot.Infrastructure.Tests.Telegram;

public class DashboardReportFormatterTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Stats_MergesInMemoryCountersWithDurableAllTimeTotals()
    {
        var metrics = new RepostMetrics(new FakeTimeProvider(Now));
        metrics.RecordMediaRepost("YtDlpPlatformExtractor");
        metrics.RecordMediaRepost("YtDlpPlatformExtractor");
        metrics.RecordMediaRepost("YtDlpPlatformExtractor");
        metrics.RecordTextRepost();
        metrics.RecordFailure("YtDlpPlatformExtractor");
        metrics.RecordFailure("YtDlpPlatformExtractor");
        metrics.RecordSilentSkip();

        var allTime = new RepostStatsSnapshot(
            TotalProcessed: 10, MediaReposts: 5, TextFallbacks: 1,
            Failures: 3, NothingExtracted: 1, NoExtractor: 0, DistinctChats: 2,
            FirstEventAt: new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero),
            LastEventAt: Now);

        var report = DashboardReportFormatter.Stats(metrics, TimeSpan.FromHours(1) + TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(3), allTime);

        report.Should().Contain("Uptime: 01:02:03");
        report.Should().Contain("Since boot — media 3, text 1, failures 2, skipped 1");
        report.Should().Contain("YtDlpPlatformExtractor: 5"); // 3 media + 2 failures
        report.Should().Contain("All-time (since 2026-06-20): 10 processed");
        report.Should().Contain("60.0% success — media 5, text 1");
        report.Should().Contain("failures 3, nothing 1, no-extractor 0");
        report.Should().Contain("Chats seen: 2");
    }

    [Fact]
    public void Stats_NoDurableHistory_SaysSoInsteadOfZeros()
    {
        var metrics = new RepostMetrics(new FakeTimeProvider(Now));

        var report = DashboardReportFormatter.Stats(metrics, TimeSpan.FromMinutes(5), RepostStatsSnapshot.Empty);

        report.Should().Contain("All-time: no history recorded yet");
        report.Should().NotContain("success");
    }

    [Fact]
    public void Failures_Empty_ReturnsCheerfulNotice()
    {
        DashboardReportFormatter.Failures([], Now).Should().Be("No failures recorded 🎉");
    }

    [Fact]
    public void Failures_RendersAgeHostUrlAndError()
    {
        var failures = new List<RecentFailure>
        {
            new(Now.AddMinutes(-30), "tiktok.com", "https://tiktok.com/@a/video/1", "ContentUnavailable", "video is private", "YtDlpPlatformExtractor"),
            new(Now.AddDays(-2), "instagram.com", "https://instagram.com/p/xyz", "NetworkFailure", "timed out", "InstagramApiExtractor"),
        };

        var report = DashboardReportFormatter.Failures(failures, Now);

        report.Should().Contain("Last 2 failure(s)");
        report.Should().Contain("30m ago · tiktok.com");
        report.Should().Contain("https://tiktok.com/@a/video/1");
        report.Should().Contain("ContentUnavailable: video is private");
        report.Should().Contain("2d ago · instagram.com");
        report.Should().Contain("NetworkFailure: timed out");
    }

    [Fact]
    public void Failures_ClipsAnOverlongErrorReason()
    {
        var hugeReason = new string('x', 500);
        var failures = new List<RecentFailure>
        {
            new(Now, "tiktok.com", "https://tiktok.com/x", "ToolFailure", hugeReason, "YtDlpPlatformExtractor"),
        };

        var report = DashboardReportFormatter.Failures(failures, Now);

        report.Should().Contain("…");
        report.Should().NotContain(hugeReason); // the full 500-char dump never makes it in
    }

    [Fact]
    public void Failures_CapsAtTelegramMessageLimit()
    {
        var many = Enumerable.Range(0, 300)
            .Select(i => new RecentFailure(
                Now.AddMinutes(-i), "tiktok.com",
                $"https://tiktok.com/@user/video/{i:0000000000}", "ToolFailure",
                "yt-dlp failed to extract this one", "YtDlpPlatformExtractor"))
            .ToList();

        var report = DashboardReportFormatter.Failures(many, Now);

        report.Length.Should().BeLessThanOrEqualTo(4096);
        report.Should().EndWith("…");
    }

    [Fact]
    public void Top_Empty_ReturnsNoData()
    {
        DashboardReportFormatter.Top([], [], failureRateMinVolume: 3).Should().Be("No platform data yet.");
    }

    [Fact]
    public void Top_RendersVolumeRankingAndFailureRateRanking()
    {
        var byVolume = new List<HostStat>
        {
            new("tiktok.com", Total: 320, Successes: 300, Failures: 10),
            new("instagram.com", Total: 110, Successes: 60, Failures: 40),
        };
        var byFailureRate = new List<HostStat>
        {
            new("instagram.com", Total: 110, Successes: 60, Failures: 44), // 40%
        };

        var report = DashboardReportFormatter.Top(byVolume, byFailureRate, failureRateMinVolume: 3);

        report.Should().Contain("By volume:");
        report.Should().Contain("1. tiktok.com — 320");
        report.Should().Contain("2. instagram.com — 110");
        report.Should().Contain("By failure rate (≥3 posts):");
        report.Should().Contain("1. instagram.com — 40.0% (44/110)");
    }

    [Fact]
    public void Top_VolumeDataButNoQualifyingFailureRate_ShowsNoneYet()
    {
        var byVolume = new List<HostStat> { new("tiktok.com", Total: 2, Successes: 2, Failures: 0) };

        var report = DashboardReportFormatter.Top(byVolume, [], failureRateMinVolume: 3);

        report.Should().Contain("By failure rate (≥3 posts):");
        report.Should().Contain("(none yet)");
    }
}
