using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using LeBot.Host.Configuration;
using LeBot.Host.Diagnostics;
using LeBot.Infrastructure.Configuration;
using LeBot.Infrastructure.Diagnostics;

namespace LeBot.Host.Installer;

/// <summary>
/// One-shot Windows installer baked into the bot executable. Run <c>LeBot.Host.exe --install</c>
/// from a folder of your choice and the bot self-registers as a Task Scheduler entry that:
///
///   * runs as LocalSystem (no logged-in user required, survives reboots);
///   * triggers at boot and starts immediately on creation;
///   * restarts once a minute for up to 999 attempts on crash.
///
/// The installer also creates the runtime folders, writes the bot token to <c>appsettings.Local.json</c>
/// and an editable <c>appsettings.json</c> next to the binary, downloads <c>yt-dlp.exe</c> if it's
/// missing, and finishes by running <c>--doctor</c> so a broken install surfaces before the operator
/// walks away. <c>--uninstall</c> removes the task; the binary and folders are left alone for the
/// operator to delete by hand.
/// </summary>
internal static class Installer
{
    internal const string TaskName = "LeBot";
    private const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";

    public static async Task<int> Run(string[] args)
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
            "--install" => await DoInstall(),
            "--uninstall" => DoUninstall(),
            _ => 1,
        };
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task<int> DoInstall()
    {
        Doctor.EnableUtf8Output();
        Console.WriteLine("=== LeBot installer ===");
        Console.WriteLine();

        var exePath = GetCurrentExePath();
        var workDir = Path.GetDirectoryName(exePath)
            ?? throw new InvalidOperationException("Cannot determine installer working directory");
        Console.WriteLine($"Install location: {workDir}");

        // We persist the token into appsettings.Local.json next to the binary instead of an
        // environment variable. Task Scheduler's service captures its env block at boot and
        // doesn't refresh when we write to HKLM — the bot launched as LocalSystem would never
        // see a freshly-set machine-scope variable. A file on disk sidesteps the whole problem
        // and Host.CreateApplicationBuilder already loads appsettings.Local.json by default
        // (Program.cs wires it explicitly so the load order is predictable).
        var configPath = Path.Combine(workDir, "appsettings.Local.json");
        var existingToken = TryReadExistingToken(configPath);
        string token;
        if (!string.IsNullOrWhiteSpace(existingToken))
        {
            Console.WriteLine($"✓ Token already in {Path.GetFileName(configPath)} — reusing.");
            token = existingToken;
        }
        else
        {
            Console.Write("Telegram bot token (from @BotFather): ");
            token = (Console.ReadLine() ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(token))
            {
                Console.Error.WriteLine("No token entered — aborting.");
                return 1;
            }

            WriteTokenConfig(configPath, token);
            Console.WriteLine($"✓ Token saved to {Path.GetFileName(configPath)}.");
        }

        EnsureDirectory(Path.Combine(workDir, "tools", "yt-dlp"));
        EnsureDirectory(Path.Combine(workDir, "downloads"));
        EnsureDirectory(Path.Combine(workDir, "logs"));
        Console.WriteLine("✓ Folders created.");

        // Drop an editable appsettings.json next to the exe if one isn't already there. The binary
        // embeds these same defaults — logging works even without this file — but a visible copy lets
        // the operator tune Serilog / YtDlp without a rebuild. appsettings.Local.json still overrides it.
        var appSettingsPath = Path.Combine(workDir, "appsettings.json");
        if (File.Exists(appSettingsPath))
        {
            Console.WriteLine($"✓ {Path.GetFileName(appSettingsPath)} already present.");
        }
        else
        {
            File.WriteAllText(appSettingsPath, EmbeddedAppConfiguration.ReadDefaults());
            Console.WriteLine($"✓ Wrote default {Path.GetFileName(appSettingsPath)}.");
        }

        var ytDlpPath = Path.Combine(workDir, "tools", "yt-dlp", "yt-dlp.exe");
        if (!File.Exists(ytDlpPath))
        {
            Console.Write("Downloading yt-dlp.exe ... ");
            try
            {
                await DownloadYtDlp(ytDlpPath);
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

        // Resolve the log path through the same code the running bot uses, so what we print here is
        // exactly where the logs will land — beside the binary, not in whatever CWD this admin shell had.
        var configuration = StandaloneConfiguration.ForExecutable(workDir);
        var logDirectory = LogPathResolver.ResolveLogDirectory(configuration, workDir);
        Console.WriteLine($"Logs:        {logDirectory}");
        Console.WriteLine($"Stop:        Stop-ScheduledTask -TaskName {TaskName}");
        Console.WriteLine($"Uninstall:   {Path.GetFileName(exePath)} --uninstall");

        // Verify the install before the operator walks away. The bot runs as LocalSystem, so evaluate
        // the cookies/account check against that identity — not the elevated admin running --install.
        // Bound the network/process probes so a hung getMe can't wedge the installer.
        Console.WriteLine();
        Console.WriteLine("Post-install checks (--doctor):");
        Console.WriteLine();
        using var doctorTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var checks = await Doctor.GatherAsync(
            configuration,
            workDir,
            doctorTimeout.Token,
            new FixedHostAccountInfo(isLocalSystem: true));
        Doctor.WriteReport(Console.Out, checks);

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
    internal static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static int RelaunchElevated(string[] args)
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

    internal static string GetCurrentExePath()
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

    private static string? TryReadExistingToken(string configPath)
    {
        if (!File.Exists(configPath))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            if (doc.RootElement.TryGetProperty("Telegram", out var telegram)
                && telegram.TryGetProperty("BotToken", out var token)
                && token.ValueKind == JsonValueKind.String)
            {
                return token.GetString();
            }
        }
        catch
        {
            // Malformed file — fall through and rewrite from scratch.
        }
        return null;
    }

    private static void WriteTokenConfig(string configPath, string token)
    {
        // Preserve any other keys the operator may have added by hand (Serilog overrides,
        // YtDlp tweaks, etc.) — only update Telegram.BotToken.
        var existing = new Dictionary<string, JsonElement>();
        if (File.Exists(configPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    existing[prop.Name] = prop.Value.Clone();
                }
            }
            catch
            {
                existing.Clear();
            }
        }

        using var stream = File.Create(configPath);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        foreach (var (key, value) in existing)
        {
            if (key.Equals("Telegram", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            writer.WritePropertyName(key);
            value.WriteTo(writer);
        }
        writer.WritePropertyName("Telegram");
        writer.WriteStartObject();
        writer.WriteString("BotToken", token);
        writer.WriteEndObject();
        writer.WriteEndObject();
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
    internal static void RunSchtasks(string arguments)
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
