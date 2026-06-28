using System.Diagnostics;
using System.Runtime.Versioning;
using LeBot.Host.Installer;

namespace LeBot.Host.Updater;

/// <summary>
/// The detached relaunch helper spawned by the updater after the binary swap (ADR&#160;0002,
/// Decision&#160;3). It waits for the old process to exit — releasing the <c>.bak</c> lock — then
/// runs <c>schtasks /Run</c> so the new binary comes up under LocalSystem with the install dir as
/// its working directory. It must NOT <c>Process.Start</c> the exe directly: that would lose the
/// LocalSystem identity and the working directory and escape Task Scheduler supervision.
/// </summary>
internal static class ApplyUpdate
{
    private const int ParentExitTimeoutMs = 60_000;

    public static int Run(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("--apply-update is Windows-only.");
            return 1;
        }

        return RunWindows(args);
    }

    [SupportedOSPlatform("windows")]
    private static int RunWindows(string[] args)
    {
        var parentPid = ParseParentPid(args);
        if (parentPid is { } pid)
        {
            WaitForParentExit(pid);
        }

        try
        {
            Installer.Installer.RunSchtasks($"/Run /TN \"{Installer.Installer.TaskName}\"");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to relaunch the bot via schtasks: {ex.Message}");
            return 1;
        }
    }

    private static int? ParseParentPid(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--parent-pid", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], out var pid))
            {
                return pid;
            }
        }

        return null;
    }

    private static void WaitForParentExit(int pid)
    {
        try
        {
            using var parent = Process.GetProcessById(pid);
            parent.WaitForExit(ParentExitTimeoutMs);
        }
        catch (ArgumentException)
        {
            // The parent already exited — nothing to wait for.
        }
    }
}
