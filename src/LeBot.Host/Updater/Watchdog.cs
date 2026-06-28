using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.Versioning;
using LeBot.Application.Releases;
using LeBot.Domain.Common;
using LeBot.Infrastructure.Maintenance;

namespace LeBot.Host.Updater;

/// <summary>
/// Early-startup self-heal for a freshly-applied update that crash-loops (ADR&#160;0002,
/// Decision&#160;5). Runs on the normal launch path before the host is even built, so it catches a new
/// binary that dies during composition — before the in-process health gate ever opens. It counts boot
/// attempts of the pending version that never reached "serving"; once that count crosses the configured
/// threshold it restores the previous binary from <c>.bak</c> (two same-volume renames, legal because
/// the earlier crashed instances have already exited) and hands off to the relaunch helper.
/// </summary>
internal static class Watchdog
{
    private const string ApplyUpdateVerb = "--apply-update";
    private const string ParentPidFlag = "--parent-pid";

    /// <summary>
    /// Inspects the pending-update state and self-heals a crash-loop if needed. Returns a process exit
    /// code to honour when a rollback was triggered (the host must stop so the helper can relaunch the
    /// restored binary), or <c>null</c> when startup should proceed normally. Fail-open: any error
    /// resolves to <c>null</c> so a watchdog fault can never wedge the bot's own startup.
    /// </summary>
    public static int? CheckOnStartup()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return CheckOnStartupWindows();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Update watchdog skipped (non-fatal): {ex.Message}");
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static int? CheckOnStartupWindows()
    {
        if (!File.Exists(UpdatePaths.MarkerPath))
        {
            return null;
        }

        var current = ResolveCurrentVersion();
        var pending = ReadVersionFile(UpdatePaths.MarkerPath);
        var pendingMatchesCurrent = current is not null && pending is not null && pending == current;

        var stampVersion = ReadVersionFile(UpdatePaths.HealthStampPath);
        var healthStampPresent = stampVersion is not null && stampVersion == current;

        var bootAttempts = ReadBootAttempts();
        if (pendingMatchesCurrent && !healthStampPresent)
        {
            bootAttempts++;
            WriteBootAttempts(bootAttempts);
        }

        var threshold = ReadMaxBootAttempts();
        var decision = UpdateWatchdog.Evaluate(
            pendingMatchesCurrent: pendingMatchesCurrent,
            isHealthy: false,
            healthStampPresent: healthStampPresent,
            healthDeadlinePassed: bootAttempts > threshold,
            backupAvailable: File.Exists(UpdatePaths.BackupPath));

        if (decision != WatchdogDecision.RollBack)
        {
            return null;
        }

        Console.Error.WriteLine(
            $"Update watchdog: v{pending} failed to start serving in {bootAttempts} boots; rolling back.");
        return RollBack();
    }

    [SupportedOSPlatform("windows")]
    private static int RollBack()
    {
        var currentExe = UpdatePaths.CurrentExePath;

        TryDelete(UpdatePaths.FailedPath);
        File.Move(currentExe, UpdatePaths.FailedPath);
        File.Move(UpdatePaths.BackupPath, currentExe);

        TryDelete(UpdatePaths.MarkerPath);
        TryDelete(UpdatePaths.HealthStampPath);
        TryDelete(UpdatePaths.BootAttemptsPath);

        // Prefer the relaunch helper (immediate, matches the apply path). If it can't be spawned, exit
        // non-zero so Task Scheduler's RestartOnFailure brings the now-restored binary back up instead.
        if (TrySpawnRelaunchHelper(currentExe))
        {
            Console.Error.WriteLine("Update watchdog: restored previous binary; relaunch helper launched.");
            return 0;
        }

        Console.Error.WriteLine("Update watchdog: restored previous binary; deferring relaunch to RestartOnFailure.");
        return 1;
    }

    [SupportedOSPlatform("windows")]
    private static bool TrySpawnRelaunchHelper(string exePath)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = UpdatePaths.InstallDirectory,
            };
            startInfo.ArgumentList.Add(ApplyUpdateVerb);
            startInfo.ArgumentList.Add(ParentPidFlag);
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

            return Process.Start(startInfo) is not null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Update watchdog: could not launch relaunch helper: {ex.Message}");
            return false;
        }
    }

    private static int ReadMaxBootAttempts()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(UpdatePaths.InstallDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Local.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var value = config.GetValue("Update:HealthGateMaxBootAttempts", 3);
        return value > 0 ? value : 3;
    }

    private static ReleaseVersion? ResolveCurrentVersion()
    {
        var raw = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (raw is not null)
        {
            var plus = raw.IndexOf('+', StringComparison.Ordinal);
            if (plus >= 0)
            {
                raw = raw[..plus];
            }
        }

        return ReleaseVersion.Parse(raw).Match<ReleaseVersion?>(version => version, _ => null);
    }

    private static ReleaseVersion? ReadVersionFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var raw = File.ReadAllText(path).Trim();
            return ReleaseVersion.Parse(raw).Match<ReleaseVersion?>(version => version, _ => null);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static int ReadBootAttempts()
    {
        try
        {
            if (!File.Exists(UpdatePaths.BootAttemptsPath))
            {
                return 0;
            }

            var raw = File.ReadAllText(UpdatePaths.BootAttemptsPath).Trim();
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count > 0
                ? count
                : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static void WriteBootAttempts(int count)
    {
        try
        {
            File.WriteAllText(UpdatePaths.BootAttemptsPath, count.ToString(CultureInfo.InvariantCulture));
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"Update watchdog: could not record boot attempt: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"Update watchdog: could not record boot attempt: {ex.Message}");
        }
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
