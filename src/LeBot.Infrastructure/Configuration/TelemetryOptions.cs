namespace LeBot.Infrastructure.Configuration;

/// <summary>
/// Bound from the <c>Telemetry</c> section of configuration. Controls where the durable repost journal
/// (the database behind the dashboard) lives.
/// </summary>
public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    /// <summary>
    /// Path to the SQLite database file. Relative values are rebased onto the executable's own
    /// directory (see <see cref="ResolvedDatabasePath"/>), so the journal lands beside the binary
    /// regardless of the launch working directory — the same rule the logs and downloads follow.
    /// </summary>
    public string DatabasePath { get; init; } = "data/lebot.db";

    /// <summary>
    /// <see cref="DatabasePath"/> as an absolute path. A relative value is rebased onto
    /// <see cref="AppContext.BaseDirectory"/>; an absolute value passes through unchanged.
    /// </summary>
    public string ResolvedDatabasePath => Path.IsPathRooted(DatabasePath)
        ? DatabasePath
        : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, DatabasePath));
}
