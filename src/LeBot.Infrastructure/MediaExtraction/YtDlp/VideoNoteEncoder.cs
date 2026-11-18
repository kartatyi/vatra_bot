using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace LeBot.Infrastructure.MediaExtraction.YtDlp;

/// <summary>
/// Turns a recorded clip into what Telegram plays as a round video: a square H.264/AAC MP4 cut from the
/// middle of the frame, at most a minute long. The edges of a wide frame are lost — that is the shape of
/// a round video, not a bug.
/// </summary>
internal static class VideoNoteEncoder
{
    /// <summary>The circle's diameter in pixels; Telegram accepts up to 640, and 480 keeps a 30 s clip near 2 MB.</summary>
    internal const int Diameter = 480;

    /// <summary>Telegram's ceiling on a round video's length.</summary>
    internal const int MaxSeconds = 60;

    /// <summary>The ffmpeg command line that encodes <paramref name="input"/> into a round video at <paramref name="output"/>.</summary>
    internal static IReadOnlyList<string> Arguments(string input, string output, int seconds) =>
    [
        "-nostdin",
        "-v", "error",
        "-y",
        "-i", input,
        "-t", Math.Clamp(seconds, 1, MaxSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture),
        "-vf", $"crop='min(iw,ih)':'min(iw,ih)',scale={Diameter}:{Diameter},setsar=1",
        "-c:v", "libx264",
        "-preset", "veryfast",
        "-crf", "26",
        "-pix_fmt", "yuv420p",
        "-c:a", "aac",
        "-b:a", "96k",
        "-movflags", "+faststart",
        output,
    ];

    /// <summary>
    /// Runs ffmpeg to encode <paramref name="input"/> into a round video at <paramref name="output"/>.
    /// False when ffmpeg failed or ran past <paramref name="timeout"/> — in which case its process tree is
    /// killed. Cancellation of <paramref name="cancellationToken"/> kills it too, and is rethrown.
    /// </summary>
    internal static async Task<bool> EncodeAsync(
        string ffmpegPath,
        string input,
        string output,
        int seconds,
        TimeSpan timeout,
        TimeProvider timeProvider,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in Arguments(input, output, seconds))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var deadline = new CancellationTokenSource(timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception ex)
        {
            logger.LogWarning(ex, "Could not start ffmpeg at {Path}", ffmpegPath);
            return false;
        }

        if (process is null)
        {
            return false;
        }

        using (process)
        {
            // Drained alongside the wait: a full stderr pipe would block ffmpeg and look like a hang.
            var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                await process.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                KillTree(process, logger);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                logger.LogWarning("ffmpeg ran past {TimeoutSeconds}s encoding a round video; killed it", (int)timeout.TotalSeconds);
                return false;
            }

            var stderr = await errors;
            if (process.ExitCode != 0 || !File.Exists(output))
            {
                logger.LogWarning("ffmpeg could not encode a round video (exit {ExitCode}): {Detail}", process.ExitCode, stderr.Trim());
                return false;
            }

            return true;
        }
    }

    private static void KillTree(Process process, ILogger logger)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Already gone, or exiting as we got here — either way nothing is left running.
            logger.LogDebug(ex, "ffmpeg had already exited when the encode was cancelled");
        }
    }
}
