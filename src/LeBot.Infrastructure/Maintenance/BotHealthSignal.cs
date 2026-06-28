namespace LeBot.Infrastructure.Maintenance;

/// <summary>
/// In-process latch the long-poll dispatcher trips once the bot is genuinely serving (Telegram getMe
/// succeeded and the poll loop is established), and the self-updater awaits before it promotes a fresh
/// update (ADR&#160;0002, Decision&#160;5). A process merely launching is not proof it works — this is
/// the difference between "the new exe started" and "the new exe is doing its job". One-shot and
/// thread-safe; later trips are no-ops.
/// </summary>
public sealed class BotHealthSignal
{
    private readonly TaskCompletionSource _served =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>True once the bot has confirmed it is serving at least once this run.</summary>
    public bool HasServed => _served.Task.IsCompleted;

    /// <summary>Trips the latch. Safe to call repeatedly; only the first call has any effect.</summary>
    public void MarkServing() => _served.TrySetResult();

    /// <summary>
    /// Completes when the bot first confirms it is serving, or throws <see cref="OperationCanceledException"/>
    /// if <paramref name="cancellationToken"/> fires first (a health-gate timeout or shutdown).
    /// </summary>
    public Task WaitForServingAsync(CancellationToken cancellationToken) =>
        _served.Task.WaitAsync(cancellationToken);
}
