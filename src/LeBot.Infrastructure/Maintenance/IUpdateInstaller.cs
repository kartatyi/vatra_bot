using LeBot.Application.Releases;
using LeBot.Domain.Common;

namespace LeBot.Infrastructure.Maintenance;

/// <summary>
/// Performs the Windows-specific binary swap: stages a verified download next to the running exe,
/// then does the two same-volume renames and launches the detached relaunch helper.
/// </summary>
internal interface IUpdateInstaller
{
    /// <summary>
    /// Streams the release asset to <c>&lt;installDir&gt;/LeBot.Host.exe.new</c> and SHA256-verifies it
    /// against <see cref="ReleaseInfo.ExpectedSha256"/>. On success returns the staged file path;
    /// otherwise returns the reason and leaves no partial file behind.
    /// </summary>
    Task<Result<string, UpdateInstallError>> DownloadAndVerifyAsync(ReleaseInfo release, CancellationToken cancellationToken);

    /// <summary>
    /// Renames the running exe to <c>.bak</c>, moves the staged file into its place, writes the
    /// <c>.update-applied</c> marker, resets the probation state for the new version, and launches the
    /// detached <c>--apply-update</c> helper. The caller must request graceful shutdown immediately
    /// after this returns.
    /// </summary>
    void ApplyAndLaunchHelper(string stagedPath, ReleaseVersion newVersion);

    /// <summary>
    /// In-process rollback for a build that started but never confirmed health within the watchdog
    /// window. Moves the running (non-serving) exe aside to <c>.failed</c>, restores <c>.bak</c> over
    /// it, clears the pending-update state, and launches the detached <c>--apply-update</c> helper. The
    /// caller must request graceful shutdown immediately after this returns. Caller must verify a
    /// <c>.bak</c> exists first.
    /// </summary>
    void RestoreBackupAndLaunchHelper();
}
