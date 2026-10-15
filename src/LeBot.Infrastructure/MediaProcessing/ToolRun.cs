namespace LeBot.Infrastructure.MediaProcessing;

/// <summary>What one run of ffmpeg or ffprobe left behind.</summary>
/// <param name="ExitCode">The process exit code; <c>-1</c> when it never started or was cut off.</param>
/// <param name="StandardOutput">Everything written to stdout — ffprobe's JSON.</param>
/// <param name="StandardError">Everything written to stderr — ffmpeg's error lines.</param>
internal sealed record ToolRun(int ExitCode, string StandardOutput, string StandardError)
{
    public string Detail => string.IsNullOrWhiteSpace(StandardError)
        ? $"exit code {ExitCode}"
        : StandardError.Trim();

    /// <summary>ffmpeg's first error line, which names the cause; the rest is the fallout.</summary>
    public string Summary => Detail.Split('\n', 2)[0].Trim();
}
