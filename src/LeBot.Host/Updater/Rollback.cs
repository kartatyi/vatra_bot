using System.Runtime.Versioning;
using LeBot.Host.Installer;

namespace LeBot.Host.Updater;

/// <summary>
/// Manual rollback verb (ADR&#160;0002, Decision&#160;5). Stops the bot, restores the previous binary
/// from the <c>.bak</c> the updater left behind, and relaunches via Task Scheduler. Because the swap
/// is rename-based on an already-stopped process it is always legal. Needs admin to drive
/// <c>schtasks</c>, so it self-elevates the same way the installer does.
/// </summary>
internal static class Rollback
{
    private const string BackupSuffix = ".bak";
    private const string FailedSuffix = ".failed";

    public static int Run(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("--rollback is Windows-only.");
            return 1;
        }

        if (!Installer.Installer.IsRunningAsAdministrator())
        {
            return Installer.Installer.RelaunchElevated(args);
        }

        return RunWindows();
    }

    [SupportedOSPlatform("windows")]
    private static int RunWindows()
    {
        var currentExe = Installer.Installer.GetCurrentExePath();
        var backupPath = currentExe + BackupSuffix;

        if (!File.Exists(backupPath))
        {
            Console.Error.WriteLine($"Nothing to roll back: no {Path.GetFileName(backupPath)} found.");
            return 1;
        }

        try
        {
            Installer.Installer.RunSchtasks($"/End /TN \"{Installer.Installer.TaskName}\"");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"(End: {ex.Message})");
        }

        try
        {
            var failedPath = currentExe + FailedSuffix;
            TryDelete(failedPath);
            if (File.Exists(currentExe))
            {
                File.Move(currentExe, failedPath);
            }

            File.Move(backupPath, currentExe);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"Rollback file swap failed: {ex.Message}");
            return 1;
        }

        try
        {
            Installer.Installer.RunSchtasks($"/Run /TN \"{Installer.Installer.TaskName}\"");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Restored the previous binary but could not relaunch: {ex.Message}");
            return 1;
        }

        Console.WriteLine("Rolled back to the previous binary and relaunched.");
        return 0;
    }

    private static void TryDelete(string path)
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
