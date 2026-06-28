using LeBot.Application.Releases;
using LeBot.Domain.Common;

namespace LeBot.Application.Tests.Releases;

public class UpdateDecisionTests
{
    private static readonly ReleaseVersion Current = new(1, 2, 3);

    [Fact]
    public void Evaluate_Disabled_ReturnsNone()
    {
        var action = UpdateDecision.Evaluate(enabled: false, Current, new ReleaseVersion(9, 9, 9), applyMode: true);

        action.Should().Be(UpdateAction.None);
    }

    [Fact]
    public void Evaluate_LatestEqualsCurrent_ReturnsNone()
    {
        var action = UpdateDecision.Evaluate(enabled: true, Current, new ReleaseVersion(1, 2, 3), applyMode: true);

        action.Should().Be(UpdateAction.None);
    }

    [Fact]
    public void Evaluate_LatestOlderThanCurrent_ReturnsNone()
    {
        var action = UpdateDecision.Evaluate(enabled: true, Current, new ReleaseVersion(1, 2, 2), applyMode: true);

        action.Should().Be(UpdateAction.None);
    }

    [Fact]
    public void Evaluate_NewerAndApplyMode_ReturnsApply()
    {
        var action = UpdateDecision.Evaluate(enabled: true, Current, new ReleaseVersion(1, 3, 0), applyMode: true);

        action.Should().Be(UpdateAction.Apply);
    }

    [Fact]
    public void Evaluate_NewerAndNotifyMode_ReturnsNotify()
    {
        var action = UpdateDecision.Evaluate(enabled: true, Current, new ReleaseVersion(1, 3, 0), applyMode: false);

        action.Should().Be(UpdateAction.Notify);
    }

    [Fact]
    public void Evaluate_DisabledButNewer_StillReturnsNone()
    {
        var action = UpdateDecision.Evaluate(enabled: false, Current, new ReleaseVersion(2, 0, 0), applyMode: false);

        action.Should().Be(UpdateAction.None);
    }
}
