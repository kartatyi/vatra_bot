namespace LeBot.Application.Releases;

/// <summary>
/// The pure policy behind the self-update health gate (ADR&#160;0002, Decision&#160;5). It turns the
/// observable state of a pending update — is this binary the one we just applied, is it serving, did
/// an earlier boot already prove healthy, has it run out of chances, can we still fall back — into a
/// single <see cref="WatchdogDecision"/>. Kept free of I/O, clocks, and the OS so the whole truth
/// table is unit-testable without touching the filesystem or Task Scheduler.
/// </summary>
public static class UpdateWatchdog
{
    /// <summary>
    /// Decides whether to promote, roll back, or keep waiting on a pending update.
    /// </summary>
    /// <param name="pendingMatchesCurrent">
    /// True when an update marker is present <em>and</em> names the version of the running binary — i.e.
    /// this process is the just-applied build under probation. False short-circuits to
    /// <see cref="WatchdogDecision.None"/> (no update, or we are already running the rolled-back binary).
    /// </param>
    /// <param name="isHealthy">The bot has confirmed it is serving this boot (Telegram getMe + the long-poll loop is established).</param>
    /// <param name="healthStampPresent">An earlier boot of this same pending version already proved healthy, so a promotion is merely outstanding — never roll back over it.</param>
    /// <param name="healthDeadlinePassed">The new version has used up its chance to prove health — its boot-attempt budget or its in-process timeout has elapsed.</param>
    /// <param name="backupAvailable">A previous binary (<c>.bak</c>) still exists to roll back to.</param>
    public static WatchdogDecision Evaluate(
        bool pendingMatchesCurrent,
        bool isHealthy,
        bool healthStampPresent,
        bool healthDeadlinePassed,
        bool backupAvailable)
    {
        if (!pendingMatchesCurrent)
        {
            return WatchdogDecision.None;
        }

        if (isHealthy)
        {
            return WatchdogDecision.Promote;
        }

        if (healthStampPresent)
        {
            return WatchdogDecision.KeepWaiting;
        }

        if (healthDeadlinePassed && backupAvailable)
        {
            return WatchdogDecision.RollBack;
        }

        return WatchdogDecision.KeepWaiting;
    }
}
