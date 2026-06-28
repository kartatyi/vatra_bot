# Deployment Guide

This guide walks through moving the bot from a developer machine to a permanent host. Tested against **Windows 10 / 11** with .NET 10 runtime; the build is self-contained so the runtime does not have to be installed on the server itself.

## TL;DR (Windows server, one command)

```powershell
# On dev machine
pwsh tools/publish.ps1

# Copy publish\LeBot.Host.exe to C:\LeBot\ on the server, then in an admin prompt:
C:\LeBot\LeBot.Host.exe --install
C:\LeBot\LeBot.Host.exe --doctor    # ✓/✗ checklist — --install runs this for you at the end
```

`--install` prompts for the bot token, writes an editable `appsettings.json`, downloads `yt-dlp.exe`, creates the runtime folders next to the binary, registers a Scheduled Task that runs at boot under `LocalSystem` with restart-on-failure, starts it, and finishes with a `--doctor` health check. To remove: `LeBot.Host.exe --uninstall`.

**The lone `.exe` is enough to boot with logging.** The default config (Serilog, yt-dlp, update) is *embedded* in the binary, so even if you copy only `LeBot.Host.exe` and forget the JSON, it still starts and writes logs — no more "copied one file, got zero logs anywhere". On-disk files next to the exe override the embedded defaults when you want to tune something:

```text
embedded appsettings.json   always present (baked in)   ← base defaults, can't be forgotten
appsettings.json            optional, next to the exe   ← edit to override defaults
appsettings.Local.json      optional, next to the exe   ← wins over both; holds the token
```

The rest of this guide explains what each step does and the manual fallback for each piece.

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

To target a Linux server use `pwsh tools/publish.ps1 -Runtime linux-x64`; the resulting binary needs `+x` permission. The `--install` command is Windows-only — on Linux configure systemd by hand.

## 2. Layout on the server (handled by `--install`)

The installer does all of this automatically. If you prefer to lay it out by hand, the target shape is:

```
C:\LeBot\
  LeBot.Host.exe
  appsettings.json      # optional — defaults are embedded; --install writes it so you can edit
  tools\
    yt-dlp\
      yt-dlp.exe
      ffmpeg.exe        # optional, only required if you want max-quality DASH merges
  downloads\            # created on first run, beside the exe; files >1h old swept every 30 min
  logs\                 # created on first run, beside the exe; daily rotation, last 7 kept
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

**Cookies and `LocalSystem` don't mix.** yt-dlp borrows cookies from a *user's* browser profile, and the `LocalSystem` account `--install` registers the task under has none — it can't see your Firefox login:

```text
bad   bot runs as LocalSystem + CookiesFromBrowser=firefox  → cookies unreadable, login-gated IG posts silently fail
good  bot runs as the interactive user who logged into Firefox → cookies readable
```

So if you set `CookiesFromBrowser`, re-register the Scheduled Task to run as *that* user — the manual `Register-ScheduledTask` in §5 with your own `-UserId`, not `S-1-5-18`. You don't have to guess: `--doctor` warns when it sees cookies configured while the bot would run as `LocalSystem`, and the bot logs the same warning at startup. Without cookies, Instagram image-carousel posts fall through to the text-only reply (which is fine for the user's stated requirement).

## 5. Auto-start with Task Scheduler

`--install` registers a Scheduled Task called `LeBot` that runs at boot under `LocalSystem` (no logged-in user required) with restart-on-failure (999 attempts, 1-minute interval). Verify with:

```powershell
Get-ScheduledTask -TaskName "LeBot" | Get-ScheduledTaskInfo
```

If you want to register it by hand (for example to use a non-`LocalSystem` account, or to put the task in a custom folder), here's the equivalent PowerShell:

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

## 6. Verify it works

Run the built-in self-check (the installer runs it for you, but run it any time):

```powershell
C:\LeBot\LeBot.Host.exe --doctor
```

It prints a ✓/✗ checklist and exits non-zero if anything is broken, so a deploy script can gate on it:

```text
✓ Configuration: loaded (embedded defaults + on-disk overrides)
✓ Bot token: present
✓ Log directory: C:\LeBot\logs
✓ Browser cookies: disabled (anonymous extraction)
✗ yt-dlp: binary not found at tools\yt-dlp\yt-dlp.exe — re-run --install to download it
✗ Telegram API: getMe rejected (code 401) — revoke and reset the token if this persists
```

- **Logs** land next to the exe at `C:\LeBot\logs\lebot-<date>.log` — an absolute path, so they never scatter to whatever directory you launched from. The bot prints `Logs: <path>` on startup, and `--install` prints it at the end.
- **From Telegram**, the bot answers these commands in any chat it's a member of:
  - `/ping` — replies `🟢 OK`. Use it from your DM with the bot to confirm it's alive.
  - `/stats` — uptime and since-boot counters merged with the *durable* all-time rollup (total processed, success rate, failures, distinct chats, since when) read from the repost journal, so the numbers survive restarts and self-updates.
  - `/failures [N]` — the last N broken links (default 5, max 15) with each one's host, URL, and the error the extractor reported. The fastest way to see *what* is breaking.
  - `/top` — the busiest platforms by volume and the ones breaking most often (by failure rate, ignoring platforms with very few posts).

Schedule an external monitor (UptimeRobot, your own ping script) to DM `/ping` every N minutes — if it stops answering, you know to investigate.

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

The bot updates itself from GitHub Releases (see `docs/decisions/0002-self-update.md`) — you normally never touch the server.

**Cut a release (dev machine).** Tag and push:

```powershell
git tag v1.1.0
git push origin v1.1.0
```

`.github/workflows/release.yml` builds the single-file exe, computes its SHA256, and publishes a GitHub Release with `LeBot.Host.exe` + `LeBot.Host.exe.sha256`. Within `Update:CheckIntervalHours` (24 h) the running bot sees the newer tag, downloads and SHA256-verifies the asset, swaps the binary via two atomic renames, and relaunches through Task Scheduler. It keeps the previous binary as `LeBot.Host.exe.bak` until the new one **proves it is serving** (Telegram `getMe` + the first poll), and only then deletes `.bak` and DMs `Update:NotifyChatId` "updated to vX". Set `Update:Mode` to `NotifyOnly`, or `Update:Enabled` to `false`, in `appsettings.Local.json` to turn off auto-apply.

**Automatic rollback (no action needed).** A bad release heals itself two ways:

- If the new build *runs but never starts serving*, the in-process watchdog waits `Update:HealthGateMaxBootAttempts`'s sibling `Update:HealthGateTimeoutMinutes` (default 5), then restores `.bak` and relaunches — you get a "rolled back to the previous version" DM.
- If the new build *crash-loops on startup* (dies before it can serve), an early-startup self-heal counts boots in `.update-boot-attempts`; once they exceed `Update:HealthGateMaxBootAttempts` (default 3) it restores `.bak` and hands off, leaving the bad binary as `LeBot.Host.exe.failed`.

**Manual rollback (server, admin prompt).** To force it yourself:

```powershell
C:\LeBot\LeBot.Host.exe --rollback
```

This stops the task, restores the previous binary, and relaunches.

**Manual update (fallback, if you don't use releases).**

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
