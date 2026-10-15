namespace LeBot.Infrastructure.MediaProcessing;

/// <summary>
/// Runs a command-line tool to completion. Cancelling the token must stop the tool, not merely the
/// wait. A seam so the compressor's fallback between encoders is testable without a GPU.
/// </summary>
internal delegate Task<ToolRun> ToolRunner(
    string executable,
    IReadOnlyList<string> arguments,
    CancellationToken cancellationToken);
