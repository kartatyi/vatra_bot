namespace LeBot.Infrastructure.Maintenance;

/// <summary>
/// The fixed file layout the updater works with, all relative to the running exe's directory.
/// Resolving from <see cref="Environment.ProcessPath"/> (never <c>Assembly.Location</c>, which is
/// empty under single-file publish) is correctness-critical, so it lives in one place. Public so the
/// Host's early-startup watchdog shares the exact same filenames as the in-process updater — the
/// marker the installer writes and the stamp/counter the watchdog reads are a cross-assembly contract.
/// </summary>
public static class UpdatePaths
{
    public const string StagedSuffix = ".new";
    public const string BackupSuffix = ".bak";

    /// <summary>The crashed/failed binary set aside by a rollback, mirroring the manual <c>--rollback</c> verb.</summary>
    public const string FailedSuffix = ".failed";

    public const string MarkerFileName = ".update-applied";

    /// <summary>Written once the new binary confirms it is serving; gates promotion and survives a missed watchdog window.</summary>
    public const string HealthStampFileName = ".update-health";

    /// <summary>Per-version "boot attempts since last healthy" counter that drives the early-startup self-heal.</summary>
    public const string BootAttemptsFileName = ".update-boot-attempts";

    /// <summary>The running executable's full path.</summary>
    public static string CurrentExePath =>
        Environment.ProcessPath
        ?? throw new InvalidOperationException("Cannot determine the running executable path");

    /// <summary>The directory the running executable lives in — the install dir.</summary>
    public static string InstallDirectory =>
        Path.GetDirectoryName(CurrentExePath)
        ?? throw new InvalidOperationException("Cannot determine the install directory");

    public static string StagedPath => CurrentExePath + StagedSuffix;

    public static string BackupPath => CurrentExePath + BackupSuffix;

    public static string FailedPath => CurrentExePath + FailedSuffix;

    public static string MarkerPath => Path.Combine(InstallDirectory, MarkerFileName);

    public static string HealthStampPath => Path.Combine(InstallDirectory, HealthStampFileName);

    public static string BootAttemptsPath => Path.Combine(InstallDirectory, BootAttemptsFileName);
}
