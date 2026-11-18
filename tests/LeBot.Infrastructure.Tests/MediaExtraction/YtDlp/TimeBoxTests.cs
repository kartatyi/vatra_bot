using LeBot.Infrastructure.MediaExtraction.YtDlp;

namespace LeBot.Infrastructure.Tests.MediaExtraction.YtDlp;

/// <summary>
/// The guarantee behind "one link can't stall every chat": a yt-dlp call that never returns gets cut off,
/// while shutdown still reads as shutdown.
/// </summary>
public class TimeBoxTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(90);

    private readonly FakeTimeProvider _time = new();

    [Fact]
    public async Task RunAsync_FinishesInTime_ReturnsItsResult()
    {
        var result = await TimeBox.RunAsync(_ => Task.FromResult("done"), Limit, _time, CancellationToken.None);

        result.Should().Be("done");
    }

    [Fact]
    public async Task RunAsync_RunsPastTheLimit_ReturnsNull()
    {
        var run = TimeBox.RunAsync(Forever, Limit, _time, CancellationToken.None);

        _time.Advance(Limit);

        (await run).Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_StillRunningBeforeTheLimit_KeepsWaiting()
    {
        var run = TimeBox.RunAsync(Forever, Limit, _time, CancellationToken.None);

        _time.Advance(Limit - TimeSpan.FromSeconds(1));

        run.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_CallerCancels_Throws()
    {
        using var shutdown = new CancellationTokenSource();
        var run = TimeBox.RunAsync(Forever, Limit, _time, shutdown.Token);

        await shutdown.CancelAsync();

        await run.Awaiting(r => r).Should().ThrowAsync<OperationCanceledException>();
    }

    private static async Task<string> Forever(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return "never";
    }
}
