using LeBot.Infrastructure.Maintenance;

namespace LeBot.Infrastructure.Tests.Maintenance;

public class BotHealthSignalTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 18, 13, 10, 0, TimeSpan.Zero);

    [Fact]
    public void LoopFields_BeforeTheFirstRound_AreNull()
    {
        var health = new BotHealthSignal();

        health.LoopTurnedAt.Should().BeNull();
        health.PolledOkAt.Should().BeNull();
    }

    [Fact]
    public void MarkLoopTurned_TelegramAnswered_MovesBothTimes()
    {
        var health = new BotHealthSignal();

        health.MarkLoopTurned(At, polledOk: true);

        health.LoopTurnedAt.Should().Be(At);
        health.PolledOkAt.Should().Be(At);
    }

    [Fact]
    public void MarkLoopTurned_TelegramUnreachable_MovesOnlyTheLoopTime()
    {
        var health = new BotHealthSignal();
        health.MarkLoopTurned(At, polledOk: true);

        health.MarkLoopTurned(At.AddMinutes(5), polledOk: false);

        health.LoopTurnedAt.Should().Be(At.AddMinutes(5));
        health.PolledOkAt.Should().Be(At, "an error round is the loop alive, not Telegram answering");
    }
}
