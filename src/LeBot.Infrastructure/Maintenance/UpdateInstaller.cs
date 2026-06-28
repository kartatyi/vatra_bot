using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using LeBot.Application.Releases;
using LeBot.Domain.Common;
using Microsoft.Extensions.Logging;

namespace LeBot.Infrastructure.Maintenance;

/// <summary>
/// Implements the Windows self-replace mechanics from ADR&#160;0002. The download is staged next to
/// the running exe (same volume — a cross-volume move is non-atomic) and SHA256-verified before any
/// swap. The swap itself is two same-volume renames, legal on a live process; the new binary is then
/// relaunched by a detached helper rather than restarted in place.
/// </summary>
internal sealed class UpdateInstaller(ILogger<UpdateInstaller> logger) : IUpdateInstaller
{
    private const string ApplyUpdateVerb = "--apply-update";
    private const string ParentPidFlag = "--parent-pid";
    private const string DownloadUserAgent = "LeBot-SelfUpdater";

    public async Task<Result<string, UpdateInstallError>> DownloadAndVerifyAsync(
        ReleaseInfo release,
        CancellationToken cancellationToken)
    {
        var stagedPath = UpdatePaths.StagedPath;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(DownloadUserAgent);

            using var response = await http.GetAsync(
                release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Downloading update asset {Url} returned HTTP {Status}",
                    release.AssetUrl, (int)response.StatusCode);
                return Result<string, UpdateInstallError>.Failure(UpdateInstallError.DownloadFailed);
            }

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = File.Create(stagedPath))
            {
                await source.CopyToAsync(destination, cancellationToken);
            }

            var actual = await ComputeSha256Async(stagedPath, cancellationToken);
            if (!string.Equals(actual, release.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogError(
                    "Update SHA256 mismatch for {Version}: expected {Expected}, got {Actual}",
                    release.Version, release.ExpectedSha256, actual);
                TryDelete(stagedPath);
                return Result<string, UpdateInstallError>.Failure(UpdateInstallError.ShaMismatch);
            }

            logger.LogInformation("Staged and verified update {Version} at {Path}", release.Version, stagedPath);
            return Result<string, UpdateInstallError>.Success(stagedPath);
        }
        catch (OperationCanceledException)
        {
            TryDelete(stagedPath);
            throw;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Update download failed for {Url}", release.AssetUrl);
            TryDelete(stagedPath);
            return Result<string, UpdateInstallError>.Failure(UpdateInstallError.DownloadFailed);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not write staged update to {Path}", stagedPath);
            TryDelete(stagedPath);
            return Result<string, UpdateInstallError>.Failure(UpdateInstallError.WriteFailed);
        }
    }

    [SupportedOSPlatform("windows")]
    public void ApplyAndLaunchHelper(string stagedPath, ReleaseVersion newVersion)
    {
        var currentExe = UpdatePaths.CurrentExePath;
        var backupPath = UpdatePaths.BackupPath;

        TryDelete(backupPath);
        File.Move(currentExe, backupPath);
        File.Move(stagedPath, currentExe);
        File.WriteAllText(UpdatePaths.MarkerPath, newVersion.ToString());

        var startInfo = new ProcessStartInfo
        {
            FileName = currentExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = UpdatePaths.InstallDirectory,
        };
        startInfo.ArgumentList.Add(ApplyUpdateVerb);
        startInfo.ArgumentList.Add(ParentPidFlag);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Process.Start(startInfo);
        logger.LogInformation("Applied update to {Version}; launched relaunch helper", newVersion);
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Could not delete {Path}", path);
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogDebug(ex, "Could not delete {Path}", path);
        }
    }
}
