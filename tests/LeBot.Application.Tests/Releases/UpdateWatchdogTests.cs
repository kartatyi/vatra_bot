using LeBot.Application.Releases;

namespace LeBot.Application.Tests.Releases;

public class UpdateWatchdogTests
{
    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, true, true)]
    public void Evaluate_NoPendingUpdateForThisBinary_ReturnsNone(
        bool isHealthy, bool stamp, bool deadlinePassed, bool backup)
    {
        var decision = UpdateWatchdog.Evaluate(
            pendingMatchesCurrent: false,
            isHealthy: isHealthy,
            healthStampPresent: stamp,
            healthDeadlinePassed: deadlinePassed,
            backupAvailable: backup);

        decision.Should().Be(WatchdogDecision.None);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    public void Evaluate_PendingAndServing_PromotesRegardlessOfTheRest(
        bool stamp, bool deadlinePassed, bool backup)
    {
        var decision = UpdateWatchdog.Evaluate(
            pendingMatchesCurrent: true,
            isHealthy: true,
            healthStampPresent: stamp,
            healthDeadlinePassed: deadlinePassed,
            backupAvailable: backup);

        decision.Should().Be(WatchdogDecision.Promote);
    }

    [Fact]
    public void Evaluate_DeadlinePassedWithBackup_RollsBack()
    {
        var decision = UpdateWatchdog.Evaluate(
            pendingMatchesCurrent: true,
            isHealthy: false,
            healthStampPresent: false,
            healthDeadlinePassed: true,
            backupAvailable: true);

        decision.Should().Be(WatchdogDecision.RollBack);
    }

    [Fact]
    public void Evaluate_DeadlinePassedButNoBackup_KeepsWaiting()
    {
        var decision = UpdateWatchdog.Evaluate(
            pendingMatchesCurrent: true,
            isHealthy: false,
            healthStampPresent: false,
            healthDeadlinePassed: true,
            backupAvailable: false);

        decision.Should().Be(WatchdogDecision.KeepWaiting);
    }

    [Fact]
    public void Evaluate_WithinDeadline_KeepsWaiting()
    {
        var decision = UpdateWatchdog.Evaluate(
            pendingMatchesCurrent: true,
            isHealthy: false,
            healthStampPresent: false,
            healthDeadlinePassed: false,
            backupAvailable: true);

        decision.Should().Be(WatchdogDecision.KeepWaiting);
    }

    [Fact]
    public void Evaluate_HealthStampFromEarlierBoot_NeverRollsBackEvenPastDeadline()
    {
        var decision = UpdateWatchdog.Evaluate(
            pendingMatchesCurrent: true,
            isHealthy: false,
            healthStampPresent: true,
            healthDeadlinePassed: true,
            backupAvailable: true);

        decision.Should().Be(WatchdogDecision.KeepWaiting);
    }
}
