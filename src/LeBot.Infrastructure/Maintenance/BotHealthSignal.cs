namespace LeBot.Infrastructure.Maintenance;

/// <summary>
/// In-process health of the long-poll dispatcher. Two signals:
/// <list type="bullet">
/// <item>A one-shot latch tripped once the bot is genuinely serving (Telegram getMe succeeded and the poll
/// loop is established), which the self-updater awaits before it promotes a fresh update (ADR&#160;0002,
/// Decision&#160;5). A process merely launching is not proof it works — this is the difference between
/// "the new exe started" and "the new exe is doing its job".</item>
/// <item>When the poll loop last turned, and when Telegram last answered it — what
/// <see cref="HeartbeatWriter"/> hands to the watchdog, which can't otherwise tell a polling bot from a
/// process that is alive but stuck.</item>
/// </list>
/// Thread-safe throughout.
/// </summary>
public sealed class BotHealthSignal
{
    private readonly TaskCompletionSource _served =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // UTC ticks; 0 means "not yet". Read and written with Interlocked: the loop and the writer run on different threads.
    private long _loopTurnedAtTicks;
    private long _polledOkAtTicks;

    /// <summary>True once the bot has confirmed it is serving at least once this run.</summary>
    public bool HasServed => _served.Task.IsCompleted;

    /// <summary>When the poll loop last finished a round, successful or not; null before the first.</summary>
    public DateTimeOffset? LoopTurnedAt => FromTicks(Interlocked.Read(ref _loopTurnedAtTicks));

    /// <summary>When Telegram last answered a poll; null before the first answer.</summary>
    public DateTimeOffset? PolledOkAt => FromTicks(Interlocked.Read(ref _polledOkAtTicks));

    /// <summary>Trips the latch. Safe to call repeatedly; only the first call has any effect.</summary>
    public void MarkServing() => _served.TrySetResult();

    /// <summary>
    /// Records a finished round of the poll loop at <paramref name="at"/>. <paramref name="polledOk"/> is
    /// whether Telegram answered: a loop that keeps turning on errors is alive but can't reach Telegram,
    /// which a restart wouldn't fix.
    /// </summary>
    public void MarkLoopTurned(DateTimeOffset at, bool polledOk)
    {
        Interlocked.Exchange(ref _loopTurnedAtTicks, at.UtcTicks);
        if (polledOk)
        {
            Interlocked.Exchange(ref _polledOkAtTicks, at.UtcTicks);
        }
    }

    /// <summary>
    /// Completes when the bot first confirms it is serving, or throws <see cref="OperationCanceledException"/>
    /// if <paramref name="cancellationToken"/> fires first (a health-gate timeout or shutdown).
    /// </summary>
    public Task WaitForServingAsync(CancellationToken cancellationToken) =>
        _served.Task.WaitAsync(cancellationToken);

    private static DateTimeOffset? FromTicks(long ticks) =>
        ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
}
