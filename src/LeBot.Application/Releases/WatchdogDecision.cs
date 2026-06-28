namespace LeBot.Application.Releases;

/// <summary>
/// What the health-gate watchdog should do with a freshly-applied update that is awaiting proof it
/// can actually serve (ADR&#160;0002, Decision&#160;5).
/// </summary>
public enum WatchdogDecision
{
    /// <summary>No update is in flight, or the running binary is not the one the pending update produced — nothing to do.</summary>
    None,

    /// <summary>The new version has not proven itself yet but still has budget left — leave the <c>.bak</c> in place and keep watching.</summary>
    KeepWaiting,

    /// <summary>The new version ran out of budget without serving — restore the previous binary from <c>.bak</c>.</summary>
    RollBack,

    /// <summary>The new version is serving — delete the <c>.bak</c> and commit to it.</summary>
    Promote,
}
