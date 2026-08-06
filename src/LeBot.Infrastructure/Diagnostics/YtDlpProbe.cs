using System.Diagnostics;
using LeBot.Infrastructure.Configuration;

namespace LeBot.Infrastructure.Diagnostics;

/// <summary>
/// The <c>--doctor</c> check that the yt-dlp binary the bot will actually use is present and runnable.
/// Resolves the path exactly the way <see cref="MediaExtraction.YtDlp.YtDlpPlatformExtractor"/> does,
/// then runs <c>--version</c> so a missing or broken binary surfaces here instead of as a silent
/// extraction failure later.
/// </summary>
public static class YtDlpProbe
{
    public static async Task<DoctorCheck> CheckAsync(YtDlpOptions options, CancellationToken cancellationToken)
    {
        var resolved = ExecutablePathResolver.Resolve(options.BinaryPath);

        if (!File.Exists(resolved))
        {
            return DoctorCheck.Fail(
                "yt-dlp",
                $"binary not found at {resolved} — run tools/fetch-tools.ps1, or re-run --install to download it");
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(resolved, "--version")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };

            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var version = (await stdoutTask).Trim();

            return process.ExitCode == 0
                ? DoctorCheck.Pass("yt-dlp", $"{resolved} (version {version})")
                : DoctorCheck.Fail("yt-dlp", $"{resolved} --version exited with code {process.ExitCode}");
        }
        catch (OperationCanceledException)
        {
            return DoctorCheck.Fail("yt-dlp", $"{resolved} --version timed out");
        }
        catch (Exception ex)
        {
            return DoctorCheck.Fail("yt-dlp", $"could not run {resolved} --version: {ex.Message}");
        }
    }
}
