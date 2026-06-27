# Telegram Link-Forwarder Bot — Engineering Rules

> The contract for *how* we build this bot. Change a rule → change this file in the same PR.
> Read it like the agent does: a context window on a budget. Every line must change a decision, or it's cut.

## Mission

A Telegram group bot that re-posts media inline so the chat never has to leave Telegram. Drop a TikTok / Reels / YT-Shorts / Threads / X / Reddit link (anything in yt-dlp's ~1800 sites) → the bot replies with the native video or photo, caption intact.

- **Phase 1 — active.** Detect URLs in any group message, extract media, re-post. Always reply with *something*: media, else the post's text, else a "couldn't extract" ack.
- **Phase 2 — planned.** Conversational layer: per-user dossiers, mention/DM replies. See [ADR 0001](docs/decisions/0001-phase2-llm-provider.md).

Quality bar: **code you'd put on a CV.** Every rule below earns its place by keeping it there.

## Agent contract — read first

- Branch + PR. **Never push to `main`.** Branch names: `feat|fix|chore|docs/<kebab>`.
- One logical change per commit; stop at a coherent unit, don't snowball.
- **"Done" means `dotnet test` is green** — not that it builds. Run it before you claim done.
- Never `--no-verify`, never `--force`, never `--amend` a pushed commit — unless asked in this same turn.
- **No AI attribution, ever** — no `Co-Authored-By: Claude`, no `Generated with…`. The committer is the human. (Stated once; here is canonical.)
- No token, bot handle, group name, or operator identity in any committed file → [Secrets](#secrets--config).
- Requirement ambiguous in a way that changes the design? Ask before coding. Mirror the user's pace: asked for rules → write rules, don't sprint to implementation.

## Architecture — Clean Architecture, 4 layers

```
src/
  LeBot.Domain/         entities, value objects, Result<>. Pure C#. No I/O. No NuGet.
  LeBot.Application/    use-cases, ports (interfaces), DTOs. References Domain + Logging.Abstractions only.
  LeBot.Infrastructure/ adapters: Telegram, yt-dlp, EF Core, HTTP, file I/O.
  LeBot.Host/           composition root: Worker Service, config, DI. No business logic.
tests/  LeBot.{Domain,Application,Infrastructure}.Tests/
```

Arrows point inward only — enforced by `.csproj` references. Infrastructure reaches Application solely through its port interfaces; nothing points back out toward `Host`:

```
Host → Infrastructure → Application → Domain
```

One extractor per platform under `Infrastructure/MediaExtraction/<Platform>/`, behind the port [`IPlatformExtractor`](src/LeBot.Application/Ports/IPlatformExtractor.cs) (`CanHandle` + `ExtractAsync`). The resolver picks the first extractor whose `CanHandle(uri)` is true.

| Concern | Choice |
|---|---|
| Runtime · lang | .NET 10 LTS · C# 14 (nullable + implicit usings) |
| Telegram | `Telegram.Bot` ≥ 22, long-polling |
| Extraction | `yt-dlp` + `YoutubeDLSharp` |
| Logging | `Serilog` — console + rolling JSON file |
| Resilience | `Polly` v8 |
| Tests | xUnit · FluentAssertions · NSubstitute · Coverlet |
| Persistence (P2) | EF Core · SQLite → PostgreSQL |
| Host · DI · config | `Microsoft.Extensions.*` |

## Code rules

Style, naming, file-scoped namespaces, one-type-per-file, `var` usage — all enforced by [`.editorconfig`](.editorconfig) + analyzers. Run `dotnet format` before committing. **Don't restate linter-owned rules here** — the linter is deterministic and free; the agent is slow and advisory.

The rules tooling *can't* catch — get these right:

```csharp
DateTimeOffset now = _timeProvider.GetUtcNow();  // good — injectable, freezable in tests
var now = DateTime.UtcNow;                        // bad  — untestable clock → flaky tests
```
```csharp
var id = _guid.NewGuid();   // good — IGuidProvider injected → deterministic in tests
var id = Guid.NewGuid();    // bad  — non-deterministic; banned in Domain
```

- Every `!` (null-forgiving) carries an inline comment proving null is impossible. No proof → drop the `!`.
- `record` for immutable DTOs / payloads / value objects; `class` for entities with behaviour.
- `async`/`await` on every I/O path; method ends in `Async`; always thread `CancellationToken` through.

Forbidden in production — each with what to do instead:

| Don't | Why it breaks | Do instead |
|---|---|---|
| `Thread.Sleep` in async | blocks a pool thread | `await Task.Delay(…, ct)` |
| `.Result` / `.Wait()` / `.GetAwaiter().GetResult()` | sync-over-async deadlock | `await` |
| `catch (Exception) {}` | silent failure, lost update | catch typed → log → recover |
| `DateTime.Now` / `.UtcNow` | untestable time | inject `TimeProvider` |
| `Guid.NewGuid()` in Domain | non-deterministic tests | inject `IGuidProvider` |

## Errors

Domain and Application return `Result<TValue, TError>` ([`Common/Result.cs`](src/LeBot.Domain/Common/Result.cs)); the extraction path carries `ExtractionError` ([`Media/ExtractionError.cs`](src/LeBot.Domain/Media/ExtractionError.cs) — variants `UnsupportedPlatform` / `ContentUnavailable` / `NetworkFailure` / `ToolFailure`). **No exceptions for control flow.**

```csharp
return new ExtractionError.UnsupportedPlatform(url);   // good — caller pattern-matches the failure
throw new NotSupportedException(url.Host);             // bad  — control flow by exception
```

- Infrastructure surfaces expected failures as `Result`; it does not invent exception types. Transient Telegram `429`/`5xx` retry in [`TelegramBotMessenger`](src/LeBot.Infrastructure/Telegram/TelegramBotMessenger.cs) via Polly (exponential backoff + jitter, 3 attempts); permanent `4xx` falls through to a source-URL text reply.
- **Never crash on one bad update.** [`TelegramUpdateDispatcher`](src/LeBot.Infrastructure/Telegram/TelegramUpdateDispatcher.cs) wraps each update in try/catch, logs under its `CorrelationId`, and keeps polling; the poll loop itself catches and backs off 5 s. A broad `catch (Exception)` is allowed *only* here, at the boundary, and only because it logs and continues — never to swallow.

## Logging — Serilog, structured only

Never interpolate values into the template; pass them as properties:

```csharp
log.LogInformation("Extracted {Platform} media in {ElapsedMs}ms", platform, sw.ElapsedMilliseconds); // good
log.LogInformation($"Extracted {platform} media in {sw.ElapsedMilliseconds}ms");                      // bad
```

- Each update opens a logging scope carrying `CorrelationId = update.Id` at the dispatcher boundary; every line for that update is tagged with it.
- **No PII at `Information` or above** — no usernames, no message text. PII at `Debug` only, behind a flag.

| Level | Use for |
|---|---|
| `Debug` | verbose dev detail (PII-gated) |
| `Information` | lifecycle, "request handled" |
| `Warning` | recoverable — retry, fallback |
| `Error` | unhandled exception in one request |
| `Critical` | bot unhealthy, needs restart |

## Tests & CI

xUnit · FluentAssertions · NSubstitute · Coverlet. AAA, blank line between sections, one concept per test.

- **Required:** every Domain behaviour + invariant (pure, no mocks); every Application use-case (ports faked with NSubstitute); a Host DI-resolves smoke test (lives in `tests/LeBot.Infrastructure.Tests/HostCompositionSmokeTests.cs` — there is no separate Host test project).
- Names: `Method_Scenario_Expectation`, e.g. `Extract_TikTokWatermarkedUrl_ReturnsCleanMp4`. `Theory` + `InlineData` for tables.
- Don't test: private methods (go through the public API), framework internals, trivial accessors.
- Never commit `[Fact(Skip = "…")]` without an issue link in the reason — a skipped test still reports green, so neither the linter nor CI catches the rot.
- Fixtures (sample URLs, captured HTML) live in `tests/_fixtures/`.
- Coverage is gated per layer by `tools/check-coverage.ps1` (CI fails below the floor). **Target / floor** — Domain 90 %/77 %, Application 80 %/90 %, overall 70 %/36 %. Floors ratchet up only — raise one in the same PR that adds the tests clearing it, never lower it.

Run before every commit:

```bash
dotnet format                                           # style; .editorconfig is the source of truth
dotnet build --no-restore -c Release
dotnet test  --no-build   -c Release --collect:"XPlat Code Coverage"
```

CI ([`.github/workflows/ci.yml`](.github/workflows/ci.yml)) on every PR + push to `main`: restore → `dotnet format --verify-no-changes` → build (Release) → test + coverage → ReportGenerator merge → `check-coverage.ps1` floor gate. A red CI = no merge.

## Git — commits, branches, PRs

Conventional Commits. Type ∈ `feat|fix|refactor|docs|test|chore|ci|perf|style|build`; optional `(scope)`; imperative subject, lowercase, ≤ 72 chars, no period. Body explains *why*, not *what*.

```
feat(extractor): support youtube shorts urls

yt-dlp already resolves both /shorts/<id> and watch?v=…, but the resolver
needs a dedicated CanHandle so this strategy wins before the generic one.

Closes #14
```
```
fix(tg-client): retry SendVideo on 429 using server-advised delay
```

- Squash-merge to `main`; the squash subject is itself a Conventional Commit. Delete the branch after.
- One PR = one logical change. Can't say it in a sentence? Split it. Open it early — draft is fine. Template: [`.github/pull_request_template.md`](.github/pull_request_template.md).
- Beyond the [Agent contract](#agent-contract--read-first): never mix concerns in one commit.

## Secrets & config

- `appsettings.json` — committed defaults. `appsettings.Development.json` — committed *only if* it carries no secret. `appsettings.Local.json` — gitignored, your overrides.
- Dev secrets → `dotnet user-secrets`. Prod secrets → env vars (`Telegram__BotToken`, `YtDlp__CookiesFromBrowser`, …).
- **Deployment identity is private.** Bot handle, token, target group, operator — only in user-secrets and local notes. Never in `README`, this file, code, logs, error strings, or commits. (The repo's own GitHub slug isn't private — the README CI badge names it; only the *runtime* identity is.) Leak → revoke at source (`@BotFather` `/revoke`) then rewrite history before pushing.
- Never commit: `*.user`, `*.local.json`, `*.local.md`, `.env`, `secrets.json`, `appsettings.Production.json`, `docs/local/`.

yt-dlp knobs ([`YtDlpOptions`](src/LeBot.Infrastructure/Configuration/YtDlpOptions.cs), `YtDlp` section):

```jsonc
"YtDlp": {
  "BinaryPath": "tools/yt-dlp/yt-dlp.exe",  // fetched by tools/fetch-tools.ps1, not committed
  "CookiesFromBrowser": null,               // "firefox" | "chrome" | … → reaches login-gated IG/X content
  "FfmpegPath": null,                        // only for formats yt-dlp must merge/transcode
  "MaxFileSizeMb": 50                        // skip above Telegram's upload ceiling
}
```

## Docs

| File | For | Holds |
|---|---|---|
| `README.md` | visitors | what it does, quick start, stack |
| `CLAUDE.md` | agent + maintainers | this contract |
| `CONTRIBUTING.md` | contributors | local setup, workflow, graphify cadence |
| `docs/deployment.md` | operators | single-file publish + Windows deployment |
| `docs/decisions/` | maintainers | ADRs — template + "when to write one" in [`docs/decisions/README.md`](docs/decisions/README.md) |
| `docs/graphs/` | maintainers + agent | `/graphify` snapshots |

Write an ADR for any decision that's hard to reverse or surprising to a newcomer (swap the DB, add a layer, pick the LLM provider). `Domain`/`Application` public APIs get XML docs; infrastructure only where behaviour is non-obvious.

---

*Last revised: 2026-06-28.*
