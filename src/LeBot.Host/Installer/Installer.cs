using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace LeBot.Host.Installer;

/// <summary>
/// One-shot Windows installer baked into the bot executable. Run <c>LeBot.Host.exe --install</c>
/// from a folder of your choice and the bot self-registers as a Task Scheduler entry that:
///
///   * runs as LocalSystem (no logged-in user required, survives reboots);
///   * triggers at boot and starts immediately on creation;
///   * restarts once a minute for up to 999 attempts on crash.
///
/// The installer also creates the runtime folders, downloads <c>yt-dlp.exe</c> if it's missing,
/// and persists <c>Telegram__BotToken</c> + <c>DOTNET_ENVIRONMENT=Production</c> at machine scope
/// so the LocalSystem account sees them. <c>--uninstall</c> removes the task; the binary and
/// folders are left alone for the operator to delete by hand.
/// </summary>
internal static class Installer
{
    private const string TaskName = "LeBot";
    private const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";

    public static int Run(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("--install is Windows-only. On Linux configure systemd by hand; the binary itself runs fine.");
            return 1;
        }

        var command = args[0].ToLowerInvariant();
        if (!IsRunningAsAdministrator())
        {
            return RelaunchElevated(args);
        }

        return command switch
        {
            "--install" => DoInstall(),
            "--uninstall" => DoUninstall(),
            _ => 1,
        };
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int DoInstall()
    {
        Console.WriteLine("=== LeBot installer ===");
        Console.WriteLine();

        var exePath = GetCurrentExePath();
        var workDir = Path.GetDirectoryName(exePath)
            ?? throw new InvalidOperationException("Cannot determine installer working directory");
        Console.WriteLine($"Install location: {workDir}");

        var token = Environment.GetEnvironmentVariable("Telegram__BotToken", EnvironmentVariableTarget.Machine);
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.Write("Telegram bot token (from @BotFather): ");
            token = (Console.ReadLine() ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(token))
            {
                Console.Error.WriteLine("No token entered — aborting.");
                return 1;
            }

            Environment.SetEnvironmentVariable("Telegram__BotToken", token, EnvironmentVariableTarget.Machine);
            Console.WriteLine("✓ Token saved to Telegram__BotToken (machine scope).");
        }
        else
        {
            Console.WriteLine("✓ Telegram__BotToken already set at machine scope — reusing.");
        }

        Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Production", EnvironmentVariableTarget.Machine);
        Console.WriteLine("✓ DOTNET_ENVIRONMENT=Production.");

        EnsureDirectory(Path.Combine(workDir, "tools", "yt-dlp"));
        EnsureDirectory(Path.Combine(workDir, "downloads"));
        EnsureDirectory(Path.Combine(workDir, "logs"));
        Console.WriteLine("✓ Folders created.");

        var ytDlpPath = Path.Combine(workDir, "tools", "yt-dlp", "yt-dlp.exe");
        if (!File.Exists(ytDlpPath))
        {
            Console.Write("Downloading yt-dlp.exe ... ");
            try
            {
                DownloadYtDlp(ytDlpPath).GetAwaiter().GetResult();
                Console.WriteLine("done.");
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.Error.WriteLine($"Failed to download yt-dlp: {ex.Message}");
                Console.Error.WriteLine("Drop yt-dlp.exe into tools\\yt-dlp\\ manually and re-run --install.");
                return 1;
            }
        }
        else
        {
            Console.WriteLine("✓ yt-dlp.exe already present.");
        }

        var taskXmlPath = Path.Combine(Path.GetTempPath(), $"lebot-task-{Guid.NewGuid():N}.xml");
        File.WriteAllText(taskXmlPath, BuildTaskXml(exePath, workDir), System.Text.Encoding.Unicode);

        try
        {
            RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{taskXmlPath}\" /F");
            Console.WriteLine($"✓ Scheduled task \"{TaskName}\" registered.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"schtasks /Create failed: {ex.Message}");
            return 1;
        }
        finally
        {
            try { File.Delete(taskXmlPath); } catch { /* best-effort */ }
        }

        try
        {
            RunSchtasks($"/Run /TN \"{TaskName}\"");
            Console.WriteLine($"✓ Task \"{TaskName}\" started.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"(Could not start task immediately: {ex.Message}. It will run at next boot.)");
        }

        Console.WriteLine();
        Console.WriteLine("All done. The bot is now running under the LocalSystem account.");
        Console.WriteLine($"Logs:        {Path.Combine(workDir, "logs")}");
        Console.WriteLine($"Stop:        Stop-ScheduledTask -TaskName {TaskName}");
        Console.WriteLine($"Uninstall:   {Path.GetFileName(exePath)} --uninstall");
        Console.WriteLine();
        Console.WriteLine("Send /ping to the bot from any chat to confirm it's online.");
        return 0;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int DoUninstall()
    {
        Console.WriteLine("=== LeBot uninstaller ===");

        try
        {
            RunSchtasks($"/End /TN \"{TaskName}\"");
            Console.WriteLine($"✓ Task \"{TaskName}\" stopped.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"(End: {ex.Message})");
        }

        try
        {
            RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
            Console.WriteLine($"✓ Task \"{TaskName}\" removed.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"schtasks /Delete failed: {ex.Message}");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("Binary, folders, and environment variables left in place — delete them by hand if you want a full wipe.");
        return 0;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int RelaunchElevated(string[] args)
    {
        Console.WriteLine("This action needs administrator rights. A UAC prompt will appear.");

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = GetCurrentExePath(),
                Arguments = string.Join(' ', args),
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Environment.CurrentDirectory,
            };
            var proc = Process.Start(startInfo);
            if (proc is null)
            {
                Console.Error.WriteLine("Failed to launch elevated process.");
                return 1;
            }
            proc.WaitForExit();
            return proc.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Console.Error.WriteLine("UAC prompt denied — installer cannot continue.");
            return 1;
        }
    }

    private static string GetCurrentExePath()
    {
        var moduleName = Process.GetCurrentProcess().MainModule?.FileName;
        return !string.IsNullOrEmpty(moduleName) ? moduleName : Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine executable path");
    }

    private static void EnsureDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }
    }

    private static async Task DownloadYtDlp(string targetPath)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LeBot-Installer/1.0");

        using var response = await http.GetAsync(YtDlpUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var tempPath = targetPath + ".download";
        await using (var src = await response.Content.ReadAsStreamAsync())
        await using (var dest = File.Create(tempPath))
        {
            await src.CopyToAsync(dest);
        }
        File.Move(tempPath, targetPath, overwrite: true);
    }

    private static string BuildTaskXml(string exePath, string workDir)
    {
        var description = $"LeBot Telegram link-forwarder ({exePath})";
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>{System.Security.SecurityElement.Escape(description)}</Description>
                <URI>\{TaskName}</URI>
              </RegistrationInfo>
              <Triggers>
                <BootTrigger>
                  <Enabled>true</Enabled>
                </BootTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>S-1-5-18</UserId>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
                <RestartOnFailure>
                  <Interval>PT1M</Interval>
                  <Count>999</Count>
                </RestartOnFailure>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{System.Security.SecurityElement.Escape(exePath)}</Command>
                  <WorkingDirectory>{System.Security.SecurityElement.Escape(workDir)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RunSchtasks(string arguments)
    {
        var psi = new ProcessStartInfo("schtasks.exe", arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("schtasks failed to launch");
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"schtasks exited with code {process.ExitCode}: {stderr.Trim()}");
        }
    }
}
