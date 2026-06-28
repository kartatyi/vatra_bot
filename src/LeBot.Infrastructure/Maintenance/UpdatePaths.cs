namespace LeBot.Infrastructure.Maintenance;

/// <summary>
/// The fixed file layout the updater works with, all relative to the running exe's directory.
/// Resolving from <see cref="Environment.ProcessPath"/> (never <c>Assembly.Location</c>, which is
/// empty under single-file publish) is correctness-critical, so it lives in one place.
/// </summary>
internal static class UpdatePaths
{
    public const string StagedSuffix = ".new";
    public const string BackupSuffix = ".bak";
    public const string MarkerFileName = ".update-applied";

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

    public static string MarkerPath => Path.Combine(InstallDirectory, MarkerFileName);
}
