# 0002. Bot Self-Update from GitHub Releases

Date: 2026-06-28
Status: Proposed

## Context

yt-dlp already self-updates daily (`YtDlpUpdateService`). The bot binary itself does not — shipping a new version means running `tools/publish.ps1` on the dev machine, copying to the server, and stop/start of the task (deployment guide §9). We want the deployed bot to update itself from GitHub Releases, automatically, with operator notification.

The deploy model constrains every decision below (from `src/LeBot.Host/Installer/Installer.cs` and `docs/deployment.md`):

- Single-file self-contained win-x64 exe (`LeBot.Host.exe`), launched by a **Task Scheduler task** `LeBot` — *not* an SCM service: `BootTrigger`, principal `S-1-5-18` (LocalSystem), `RestartOnFailure` 999×/1 min, `ExecutionTimeLimit` unlimited, `MultipleInstancesPolicy=IgnoreNew`, working directory `C:\LeBot`.
- Graceful shutdown exits code 0.
- The repo is public; today there are **no releases, no git tags, and no version stamped** in `LeBot.Host.csproj`.

Three Windows facts (verified against Microsoft docs) drive the design:

1. A running exe **can be renamed/moved** on the same volume but **cannot be overwritten in place or deleted** while alive — the loader holds the image with `FILE_SHARE_READ | FILE_SHARE_DELETE`, not `WRITE`.
2. Task Scheduler `RestartOnFailure` fires only on **failure**. A graceful **exit 0 is success and is not restarted** — the bot cannot relaunch itself by exiting.
3. Single-file native-lib self-extraction goes to `%TEMP%\.net\…`, a separate directory that does not lock the main exe. Resolve the exe via `Environment.ProcessPath`, never `Assembly.Location` (empty under single-file).

## Decisions

### Decision 1 — In-app updater baked into the exe

A `SelfUpdateService : BackgroundService` (mirrors `YtDlpUpdateService`) plus a new `--apply-update` verb (sibling to `--install` / `--uninstall`).

Rejected alternatives: **Velopack** — re-tools packaging away from the bespoke single-file / Task-Scheduler model and is desktop-oriented; **external updater task** — simpler and robust, but not an in-app feature and not unit-testable. The in-app path is consistent with the existing one-exe-does-everything design and the most testable.

### Decision 2 — Versioning + release pipeline (prerequisite)

The git tag `vX.Y.Z` is the single source of version truth. `.github/workflows/release.yml` (on `push: tags: v*`, runner `windows-2022`): publish the single-file exe → compute SHA256 → create a GitHub Release with `LeBot.Host.exe` + `LeBot.Host.exe.sha256`. The build stamps `-p:Version` / `-p:InformationalVersion` from the tag; the bot reads its own version from `AssemblyInformationalVersionAttribute`. `csproj` carries a `0.0.0` dev fallback so the first real release (`v1.0.0 > 0.0.0`) triggers exactly once. **There is nothing to update from until this lands.**

### Decision 3 — Atomic swap via two same-volume renames, then explicit relaunch

The live process does the risky work while healthy, then hands off:

1. `SelfUpdateService` downloads the asset to `C:\LeBot\LeBot.Host.exe.new` (same volume as the install dir — never `%TEMP%`, since a cross-volume move is non-atomic) and SHA256-verifies it.
2. While still alive, two atomic renames: `LeBot.Host.exe → .bak`, then `.new → LeBot.Host.exe`. Both are legal on the running exe; overwrite-in-place is not.
3. Spawn the detached helper from the new binary: `LeBot.Host.exe --apply-update --parent-pid <self> …`; then request graceful stop (exit 0).
4. The helper waits for the parent PID to exit (releasing the `.bak` lock), then **`schtasks /Run /TN "LeBot"`** to relaunch under LocalSystem with the correct working directory. It must **not** `Process.Start` the exe directly — that loses LocalSystem identity, loses the `C:\LeBot` working directory (breaking every relative path), and escapes Task Scheduler supervision. `MultipleInstancesPolicy=IgnoreNew` makes the relaunch race-safe.

The explicit `schtasks /Run` is the load-bearing correction: a graceful exit 0 is *not* restarted by `RestartOnFailure`, so without it an auto-update takes the bot offline until the next reboot.

### Decision 4 — SHA256 verification, GitHub digest primary

Primary integrity source = GitHub's server-computed `asset.digest` (`sha256:…`, exposed since June 2025) read straight from the release JSON. Fallback = the `LeBot.Host.exe.sha256` asset (covers a null digest on legacy releases and human `sha256sum -c`). On mismatch: delete `.new`, log `Error`, DM the operator, abort. Never swap an unverified binary; never auto-retry the same asset (a mismatch is corruption or tampering).

### Decision 5 — Rollback gated on proven health

Keep `.bak` until the new version proves itself — on startup, after Telegram `getMe` + polling is established, it writes a health stamp. A crash-looping new exe is otherwise relaunched up to 999× by `RestartOnFailure` with no good binary left. The updater then either promotes (delete `.bak`, DM "updated to vX") or rolls back (rename `.bak → .exe`, `schtasks /Run`, DM "vX failed health check, rolled back to vY"). An early-startup self-heal check makes rollback survive even a missed watchdog window. Because rollback is rename-based on an already-exited process, it is always legal.

### Decision 6 — Automatic by default, with a notify-only safety flag

An `Update` config section: `Enabled` (default `true`), `Mode` = `Apply | NotifyOnly` (default `Apply`), `Repository`, `AssetName`, `CheckIntervalHours` (24), `StartupDelayMinutes` (2, staggered after yt-dlp's 1), `NotifyChatId` (operator DM target — private deployment identity, set in `appsettings.Local.json`, never committed). Field kill-switch: set `Mode=NotifyOnly` (notify but don't apply) or `Enabled=false` (stop checking).

## Consequences

**Positive**

- Hands-off hosts stay current without a manual copy-and-restart.
- CV-grade demonstration: a CI/CD release pipeline, non-trivial Windows self-replace mechanics, and clean port/adapter separation.
- Pure `ReleaseVersion` / `Sha256Hash` value objects lift Domain coverage; the design reuses the existing installer (`schtasks`, `Environment.ProcessPath`) and `YtDlpUpdateService` patterns.
- Reversible per decision — Velopack remains a future option if the bespoke updater ever proves too costly to maintain.

**Negative / open**

- Introduces the **first `TimeProvider.System` registration**. Three pre-existing sites violate the inject-`TimeProvider` rule (`TelegramUpdateDispatcher`, `RepostMetrics`, `DownloadsCleanupService`) and should migrate in a separate `refactor(time)` PR — not bundled here (never mix concerns).
- The detached swap/relaunch handoff is the hardest part to unit-test; it is covered by a manual/integration smoke, the rest unit-tested per layer.
- Auto-replacing a running binary is inherently sensitive — the SHA256 gate and health-gated rollback are mandatory, not optional.

## Implementation order (proposed)

- **Phase A — prerequisite.** Versioning props + `release.yml` + cut `v1.0.0`. Nothing self-updates until a release exists.
- **Phase B — core updater.** Domain value objects → Application ports/DTOs → Infrastructure `GitHubReleaseSource` / `SelfUpdateService` / `UpdateInstaller` → Host `--apply-update` verb + DI + `Update` config.
- **Phase C — safety.** Health-gate + rollback + Telegram DM, plus the separate `refactor(time)` migration.

Each phase is its own PR off `main`. The full file-by-file plan and per-layer test plan are tracked alongside this ADR.

---

*This ADR is proposed, not accepted — the user revises and flips the status before any code starts. Decision 5 (rollback depth) is the most worth pushing back on: a leaner v1 could keep `.bak` for manual rollback only and defer the health-gate/self-heal.*
