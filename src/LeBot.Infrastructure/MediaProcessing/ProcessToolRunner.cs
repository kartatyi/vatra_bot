using System.ComponentModel;
using System.Diagnostics;

namespace LeBot.Infrastructure.MediaProcessing;

/// <summary>The real <see cref="ToolRunner"/>: starts the process and kills it if cancelled.</summary>
internal static class ProcessToolRunner
{
    public static async Task<ToolRun> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            // The binary is not where the path says — the process never started.
            return new ToolRun(-1, string.Empty, $"could not start {executable}: {ex.Message}");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // WaitForExitAsync stops waiting but leaves the encoder running, still writing the file.
            KillQuietly(process);
            throw;
        }

        return new ToolRun(process.ExitCode, await standardOutput, await standardError);
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(TimeSpan.FromSeconds(5));
        }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }
}
