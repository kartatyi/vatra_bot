# 0003. First-Run Observability: Embedded Config, Exe-Relative Paths, `--doctor`

Date: 2026-06-28
Status: Accepted

## Context

A deploy went dark in the field. The operator copied only `publish\LeBot.Host.exe` to the server — not the rest of the publish folder — and the bot ran with **no feedback whatsoever**. Three compounding causes, each invisible:

1. **No config, no logging.** `appsettings.json` holds the *entire* Serilog config, and single-file publish does not embed it. Absent that file the bot had zero sinks → no log files anywhere.
2. **CWD-relative paths.** Serilog's `logs/lebot-.log`, the `downloads/` folder, and the on-disk config files all resolved against the *current working directory*. Task Scheduler sets a working directory so the real deployment is fine, but a bare-exe launch (or an elevated `--install` from `System32`) scatters or fails to find them.
3. **Silent by design + LocalSystem cookies.** Phase 1 stays silent when extraction yields nothing (correct), and `--install` runs the bot as `LocalSystem`, which has no browser profile — so `CookiesFromBrowser` extraction failed silently. With no logs, the only way to diagnose any of this was a remote shell.

The fix is not "log the failure louder" — it's "make it impossible to end up with no signal."

## Decisions

### Decision 1 — Embed the default config as the base layer

`appsettings.json` is embedded in the binary and loaded as the lowest-precedence configuration source ([`EmbeddedAppConfiguration`](../../src/LeBot.Infrastructure/Configuration/EmbeddedAppConfiguration.cs)), so Serilog/YtDlp/Update defaults always exist. On-disk files layer on top as optional overrides.

```text
embedded appsettings.json   ← base, baked in, can't be forgotten
appsettings.json (on disk)  ← optional override
appsettings.Local.json      ← optional, highest; holds the token
```

It lives in **Infrastructure**, not Host, embedded via a *linked* reference to the Host's `appsettings.json` (`LeBot.Infrastructure.csproj`). One source file, no drift — and it sits in a layer the test suite already covers, so the "lone exe still has Serilog sinks" guarantee is unit-tested without dragging Host's untested surface into the coverage floor. It's added through an in-memory source, not `AddJsonStream`: `ConfigurationManager` rebuilds every provider when a later source is added, which would replay a read-once stream and lose the defaults.

### Decision 2 — Pin runtime paths to the executable's directory, never the CWD

```csharp
ContentRootPath = AppContext.BaseDirectory          // config files + reloadOnChange watcher
LogPathResolver.ResolveAbsolutePaths(...)           // Serilog file sink → absolute, beside the exe
YtDlpOptions.ResolvedDownloadDirectory              // downloads/ → beside the exe
```

```text
bad   logs/lebot-.log relative to CWD   → lands in System32 (elevated) or vanishes
good  C:\LeBot\logs\lebot-.log absolute → always beside the binary, printed at startup
```

The launch CWD is now irrelevant: a bare-exe run from anywhere boots, logs, and downloads correctly — Task Scheduler's working directory was the only thing masking this before.

### Decision 3 — A `--doctor` verb + startup transparency

A read-only `--doctor` verb (mirrors the existing `--install`/`--rollback` dispatch) prints a ✓/✗ checklist — config loaded, token present, yt-dlp runnable, Telegram `getMe`, log dir writable, cookies sane for the account — and exits non-zero on failure. `--install` runs it at the end. At runtime the host logs one Information line summarising effective config, and escalates to a **Warning** when `CookiesFromBrowser` is set while running as `LocalSystem` — the exact trap from cause 3.

## Consequences

- **Easier:** a lone `.exe` is now a supported deployment; every startup failure is observable in a log file beside the binary or in the `--doctor` output. Diagnosis no longer needs a remote shell.
- **Locked in:** the embedded resource name (`LeBot.Infrastructure.appsettings.defaults.json`) is a build contract; the embedded and on-disk `appsettings.json` are the same linked file. `--doctor` evaluates the cookies/account check against the *service* identity (LocalSystem), which the installer passes explicitly since it isn't running as the service itself.
- **Harder:** `--doctor`'s `getMe` makes a network call, so it isn't usable fully offline; it bounds itself with a 30 s timeout and degrades each probe to a ✗ rather than hanging.
