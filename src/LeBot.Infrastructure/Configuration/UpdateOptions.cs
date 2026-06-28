namespace LeBot.Infrastructure.Configuration;

/// <summary>
/// Bound from the <c>Update</c> section of configuration. Drives the self-updater
/// (<see cref="Maintenance.SelfUpdateService"/>). <see cref="NotifyChatId"/> is private deployment
/// identity — set it in <c>appsettings.Local.json</c>, never committed.
/// </summary>
public sealed class UpdateOptions
{
    public const string SectionName = "Update";

    /// <summary>Master switch. When false the updater never checks and never notifies.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Whether to apply updates automatically or only notify the operator.</summary>
    public UpdateMode Mode { get; init; } = UpdateMode.Apply;

    /// <summary>The GitHub <c>owner/repo</c> whose releases are polled.</summary>
    public string Repository { get; init; } = "kartatyi/vatra_bot";

    /// <summary>The release asset that holds the new binary.</summary>
    public string AssetName { get; init; } = "LeBot.Host.exe";

    /// <summary>How often to poll for a newer release.</summary>
    public int CheckIntervalHours { get; init; } = 24;

    /// <summary>Delay before the first check, staggered after yt-dlp's startup update.</summary>
    public int StartupDelayMinutes { get; init; } = 2;

    /// <summary>
    /// How long a freshly-applied build has to confirm it is serving before the in-process watchdog
    /// gives up on it and rolls back to the previous binary (ADR&#160;0002, Decision&#160;5).
    /// </summary>
    public int HealthGateTimeoutMinutes { get; init; } = 5;

    /// <summary>
    /// How many times a pending build may boot without ever confirming health before the
    /// early-startup self-heal restores the previous binary — the backstop for a crash-loop that
    /// dies before the in-process watchdog window opens.
    /// </summary>
    public int HealthGateMaxBootAttempts { get; init; } = 3;

    /// <summary>Operator chat to DM about updates. Null disables notifications.</summary>
    public long? NotifyChatId { get; init; }
}
