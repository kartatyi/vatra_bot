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

    /// <summary>Operator chat to DM about updates. Null disables notifications.</summary>
    public long? NotifyChatId { get; init; }
}
