# Deployment Guide

This guide walks through moving the bot from a developer machine to a permanent host. Tested against **Windows 10 / 11** with .NET 10 runtime; the build is self-contained so the runtime does not have to be installed on the server itself.

## 1. Build the deployable

On the developer machine:

```powershell
pwsh tools/publish.ps1
```

Output lands in `publish/`:

```
publish/
  LeBot.Host.exe                # ~80 MB, single self-contained binary
  appsettings.json              # defaults, no secrets
  appsettings.Development.json  # ignored in Production
```

`PublishSingleFile=true` bundles every managed dependency into the .exe. Native libraries are extracted to a per-user temp folder on first run; `IncludeNativeLibrariesForSelfExtract=true` keeps that contract explicit.

To target a Linux server use `pwsh tools/publish.ps1 -Runtime linux-x64`; the resulting binary needs `+x` permission.

## 2. Lay out the server folder

Recommended layout on the server:

```
C:\LeBot\
  LeBot.Host.exe
  appsettings.json
  tools\
    yt-dlp\
      yt-dlp.exe
      ffmpeg.exe        # optional, only required if you want max-quality DASH merges
  downloads\            # created on first run; bot cleans up files >1h old every 30 min
  logs\                 # created on first run; daily rotation, last 7 kept
```

Fetch yt-dlp manually or via `pwsh tools/fetch-tools.ps1` from a checkout of the repo.

## 3. Configure the bot token (production)

Production should not use `dotnet user-secrets`. Set environment variables instead:

```powershell
# As the user that will run the service:
[Environment]::SetEnvironmentVariable("Telegram__BotToken", "1234567:ABCDEF...", "User")
[Environment]::SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Production", "User")
```

The double underscore (`__`) is .NET's convention for nested config keys — it maps to `Telegram:BotToken` in `appsettings.json`.

If the server runs the bot as a different account (e.g. `NT AUTHORITY\SYSTEM` via Task Scheduler), set the variables at **Machine** scope instead:

```powershell
[Environment]::SetEnvironmentVariable("Telegram__BotToken", "...", "Machine")
[Environment]::SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Production", "Machine")
```

`docs/decisions/` lists why we picked env vars over user-secrets for prod (rotation, machine-wide access, no per-user keystore).

## 4. Cookies for Instagram (optional)

If you want the bot to read Instagram image content gated behind login (rare; most posts work without), install Firefox on the server, log in to Instagram, then:

```powershell
[Environment]::SetEnvironmentVariable("YtDlp__CookiesFromBrowser", "firefox", "User")
```

Without this, Instagram image-carousel posts will fall through to the text-only reply (which is fine for the user's stated requirement).

## 5. Auto-start with Task Scheduler

Task Scheduler is the lightest path on Windows 10 — no extra software, restarts on crash, starts at boot.

### One-shot setup (PowerShell as Administrator):

```powershell
$action = New-ScheduledTaskAction -Execute "C:\LeBot\LeBot.Host.exe" -WorkingDirectory "C:\LeBot"
$trigger = New-ScheduledTaskTrigger -AtStartup
$settings = New-ScheduledTaskSettingsSet `
    -RestartCount 999 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -StartWhenAvailable `
    -ExecutionTimeLimit (New-TimeSpan -Days 365) `
    -MultipleInstances IgnoreNew

$principal = New-ScheduledTaskPrincipal `
    -UserId "$env:USERDOMAIN\$env:USERNAME" `
    -LogonType S4U `
    -RunLevel Highest

Register-ScheduledTask `
    -TaskName "LeBot" `
    -Action $action `
    -Trigger $trigger `
    -Settings $settings `
    -Principal $principal `
    -Description "Telegram link-forwarder bot"
```

Start it immediately:

```powershell
Start-ScheduledTask -TaskName "LeBot"
```

Verify:

```powershell
Get-ScheduledTask -TaskName "LeBot" | Get-ScheduledTaskInfo
```

The task restarts the bot once per minute for up to 999 attempts after any crash, and survives reboots because the trigger is `AtStartup` and `S4U` logon doesn't require an interactive session.

### Watch the logs

```powershell
Get-Content C:\LeBot\logs\lebot-*.log -Tail 20 -Wait
```

## 6. Healthcheck

The bot exposes two commands in any chat it's a member of:

- `/ping` — replies `🟢 OK`. Use this from your own DM with the bot to confirm it's alive.
- `/stats` — replies with uptime, repost counts, and per-extractor tallies since the process started.

Schedule an external monitor (UptimeRobot, your own ping script) to DM `/ping` to the bot every N minutes — if it stops responding, you know to investigate.

## 7. Graceful shutdown

`Stop-ScheduledTask` (or `taskkill /im LeBot.Host.exe`) sends SIGTERM. The bot:

1. Stops accepting new updates from Telegram (`stoppingToken` cancels).
2. Lets the in-flight extraction finish (cancellation propagates; uploads abort cleanly).
3. Disposes the chat-action indicator, the HttpClient, the metrics counters.
4. Exits with code 0.

`Task Scheduler` waits up to 30 seconds by default before force-killing. Increase with `-ExecutionTimeLimit` on the task action if you start seeing forced kills.

## 8. Token rotation

When the token leaks, expires, or you want to cycle it for hygiene:

1. **Get a new token.** In `@BotFather`: `/mybots` → pick the bot → **API Token** → **Revoke current token**. Old token dies immediately.
2. **Update the environment variable** on the server (`User` or `Machine` scope, whichever you used in step 3):
   ```powershell
   [Environment]::SetEnvironmentVariable("Telegram__BotToken", "<new-token>", "User")
   ```
3. **Restart the task:** `Stop-ScheduledTask -TaskName "LeBot"; Start-ScheduledTask -TaskName "LeBot"`.

Downtime is whatever Task Scheduler takes to stop+start — typically <10 seconds.

## 9. Updating the bot

```powershell
# On dev machine:
pwsh tools/publish.ps1

# Copy publish/ to server (robocopy, scp, etc.):
robocopy .\publish C:\LeBot LeBot.Host.exe appsettings.json /MIR

# On server:
Stop-ScheduledTask -TaskName "LeBot"
Start-ScheduledTask -TaskName "LeBot"
```

For yt-dlp updates (it's the most volatile dependency since each platform breaks something every few weeks):

```powershell
C:\LeBot\tools\yt-dlp\yt-dlp.exe -U
```

This is yt-dlp's own self-update — works even without our tooling.

## 10. Uninstall

```powershell
Stop-ScheduledTask -TaskName "LeBot"
Unregister-ScheduledTask -TaskName "LeBot" -Confirm:$false
[Environment]::SetEnvironmentVariable("Telegram__BotToken", $null, "User")
Remove-Item -Recurse -Force C:\LeBot
```
