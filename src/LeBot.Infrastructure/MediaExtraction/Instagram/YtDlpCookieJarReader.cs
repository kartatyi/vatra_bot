using System.Diagnostics;
using LeBot.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.MediaExtraction.Instagram;

/// <summary>
/// Exports the browser cookie store by invoking yt-dlp with <c>--cookies-from-browser</c>, writing a
/// temporary Netscape jar and returning its lines. We reuse yt-dlp rather than read the browser's
/// cookie DB ourselves so that profile selection, DB locks, and container cookies stay yt-dlp's
/// problem — the same machinery already proven to log the bot into Instagram for video. yt-dlp exits
/// non-zero against the homepage target (it has no media) yet still writes the jar, so the exit code
/// is ignored and the file is the source of truth.
/// </summary>
internal sealed class YtDlpCookieJarReader : IBrowserCookieJarReader
{
    // yt-dlp dumps the *entire* browser cookie store regardless of the target, so point it at a page
    // that costs nothing to "extract" rather than burning a real media lookup just to refresh cookies.
    private const string DumpTargetUrl = "https://www.instagram.com/";

    private readonly YtDlpOptions _options;
    private readonly ILogger<YtDlpCookieJarReader> _logger;

    public YtDlpCookieJarReader(IOptions<YtDlpOptions> options, ILogger<YtDlpCookieJarReader> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>?> ReadAsync(string browser, CancellationToken cancellationToken)
    {
        var jarPath = Path.Combine(Path.GetTempPath(), $"lebot_ig_cookies_{Guid.NewGuid():N}.txt");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ExecutablePathResolver.Resolve(_options.BinaryPath),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--cookies-from-browser");
            psi.ArgumentList.Add(browser);
            psi.ArgumentList.Add("--cookies");
            psi.ArgumentList.Add(jarPath);
            psi.ArgumentList.Add("--skip-download");
            psi.ArgumentList.Add("--no-warnings");
            psi.ArgumentList.Add("--playlist-items");
            psi.ArgumentList.Add("0");
            psi.ArgumentList.Add(DumpTargetUrl);

            using var process = new Process { StartInfo = psi };
            process.Start();
            // Drain both pipes so the child can't deadlock on a full buffer; the output is unused.
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdout, stderr);

            if (!File.Exists(jarPath))
            {
                _logger.LogWarning(
                    "yt-dlp wrote no cookie jar for browser {Browser}; Instagram extraction will be anonymous",
                    browser);
                return null;
            }

            return await File.ReadAllLinesAsync(jarPath, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to export {Browser} cookies via yt-dlp", browser);
            return null;
        }
        finally
        {
            BestEffortDelete(jarPath);
        }
    }

    private static void BestEffortDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
