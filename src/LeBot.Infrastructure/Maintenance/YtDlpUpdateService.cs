using System.Diagnostics;
using LeBot.Infrastructure.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeBot.Infrastructure.Maintenance;

/// <summary>
/// Keeps yt-dlp current by running its built-in <c>-U</c> self-update once a day. yt-dlp ships
/// roughly every one-to-two weeks and every other release fixes some platform that just broke,
/// so an unattended host that never updates degrades fast. The self-update is atomic: yt-dlp
/// downloads the new binary alongside the running one and swaps on next process launch, so
/// in-flight extractions are unaffected.
/// </summary>
public sealed class YtDlpUpdateService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(2);

    private readonly YtDlpOptions _options;
    private readonly ILogger<YtDlpUpdateService> _logger;

    public YtDlpUpdateService(IOptions<YtDlpOptions> options, ILogger<YtDlpUpdateService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await UpdateOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "yt-dlp self-update sweep failed");
            }

            try
            {
                await Task.Delay(UpdateInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task UpdateOnceAsync(CancellationToken cancellationToken)
    {
        var binaryPath = ExecutablePathResolver.Resolve(_options.BinaryPath);
        if (!File.Exists(binaryPath))
        {
            _logger.LogWarning("yt-dlp not found at {Path} — skipping self-update", binaryPath);
            return;
        }

        var before = await GetVersionAsync(binaryPath, cancellationToken);
        var result = await RunAsync(binaryPath, "-U", cancellationToken);

        if (result.ExitCode != 0)
        {
            _logger.LogWarning(
                "yt-dlp -U exited with code {Code}: {Stderr}",
                result.ExitCode, result.Stderr.Trim());
            return;
        }

        var after = await GetVersionAsync(binaryPath, cancellationToken);

        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            _logger.LogDebug("yt-dlp is up to date ({Version})", before ?? "<unknown>");
        }
        else
        {
            _logger.LogInformation(
                "yt-dlp self-updated: {Before} → {After}",
                before ?? "<unknown>", after ?? "<unknown>");
        }
    }

    private static async Task<string?> GetVersionAsync(string binaryPath, CancellationToken cancellationToken)
    {
        var result = await RunAsync(binaryPath, "--version", cancellationToken);
        return result.ExitCode == 0 ? result.Stdout.Trim() : null;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string binaryPath,
        string arguments,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(binaryPath, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {binaryPath}");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ProcessTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(true); } catch { /* best-effort */ }
            return (-1, string.Empty, "yt-dlp process timed out");
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        return (process.ExitCode, stdout, stderr);
    }
}
