# Telegram Link-Forwarder Bot — Project Rules

> Single source of truth for *how* we build this bot. If you change a rule, update this file in the same PR.

## 1. Mission

A Telegram group bot that:

- **Phase 1 (active).** Detects video / photo / post URLs from supported platforms (TikTok, Instagram Reels, YouTube Shorts, Threads, etc.) in any group message and re-posts the actual media as a native Telegram message so members don't have to leave the chat.
- **Phase 2 (planned).** Conversational layer — per-user dossiers built from chat activity, replies to mentions and DMs, answers to questions, lightweight task running, friendly ribbing of members.

The quality bar: **code you'd put on a CV.** Every choice should make that easier.

## 2. Tech Stack

| Concern              | Choice                                     | Notes                                          |
|----------------------|--------------------------------------------|------------------------------------------------|
| Runtime              | .NET 10 LTS                                | Supported until November 2028.                 |
| Language             | C# 14                                      | `Nullable` and `ImplicitUsings` enabled.       |
| Telegram client      | `Telegram.Bot` (>= 22.x)                   | Long-polling first; webhook optional later.    |
| Media extraction     | `yt-dlp` + `YoutubeDLSharp`                | yt-dlp pinned to a release; auto-update opt-in.|
| Logging              | `Serilog`                                  | Structured; console + rolling file sinks.      |
| Configuration        | `Microsoft.Extensions.Configuration`       | appsettings + env vars + user-secrets.         |
| DI                   | `Microsoft.Extensions.DependencyInjection` | Standard.                                      |
| Hosting              | `Microsoft.Extensions.Hosting`             | Worker Service; `IHostedService` poll loop.    |
| Resilience           | `Polly` v8                                 | Retry / backoff on transient Telegram errors.  |
| Testing              | xUnit + FluentAssertions + NSubstitute     | Coverlet for coverage.                         |
| Persistence (P2)     | EF Core + SQLite (dev) / PostgreSQL (prod) | Migrations committed.                          |
| LLM (P2)             | TBD                                        | Decision deferred to first P2 ADR.             |

External tools:

- `yt-dlp.exe` lives under `tools/yt-dlp/` and is fetched at first build via `tools/fetch-tools.ps1` (skipped if already present). Not committed.
- `ffmpeg` optional; required only for formats yt-dlp must merge.

## 3. Architecture — Clean Architecture, 4 layers

Folder layout:

```
src/
  LeBot.Domain/         # Entities, value objects, domain events. Pure C#, no I/O.
  LeBot.Application/    # Use-cases, ports (interfaces), DTOs, orchestration. No infra.
  LeBot.Infrastructure/ # Adapters: Telegram, yt-dlp, EF Core, HTTP, file I/O.
  LeBot.Host/           # Composition root. Worker Service. Config. DI wiring.
tests/
  LeBot.Domain.Tests/
  LeBot.Application.Tests/
  LeBot.Infrastructure.Tests/
```

Dependency rule, enforced by `csproj` references:

```
Host  ->  Infrastructure  ->  Application  ->  Domain
                                   ^
                            (via interfaces only)
```

- `Domain` references no external NuGet packages.
- `Application` references `Domain` and `Microsoft.Extensions.Logging.Abstractions` only.
- `Infrastructure` references `Application` and external libraries.
- `Host` wires the graph; no business logic lives here.

Key pattern — media platforms:

```csharp
// in Application
public interface IPlatformExtractor
{
    bool CanHandle(Uri url);
    Task<MediaPayload> ExtractAsync(Uri url, CancellationToken ct);
}
```

One implementation per platform under `Infrastructure/MediaExtraction/<Platform>/`. A resolver picks the first `CanHandle`.

## 4. Code Style

We follow the .NET Runtime coding guidelines with the choices below (enforced by `.editorconfig`).

Language and layout:

- **File-scoped namespaces.** Always.
- **One public type per file.** File name equals type name.
- **`var`** when the right-hand side makes the type obvious; otherwise explicit.
- **Records** for immutable DTOs, value objects, message payloads. Classes for entities with behaviour.
- **`async` / `await`** for every I/O path. Methods end with `Async`. Always pass `CancellationToken`.
- **Nullable reference types** on. The null-forgiving `!` operator requires an inline comment justifying it.
- **Primary constructors** for DI-injected services.
- **Pattern matching** over chained `if / else` on type or shape.
- **Collection expressions** (`[]`, `[a, ..b, c]`) when clearer than `new List<T>()`.
- **`required` properties** when most properties are mandatory; skip the constructor.
- **No `#region`.** No redundant `this.`.
- **No comments** unless they explain a non-obvious *why* (an invariant, a workaround, a gotcha). Code says *what*; names say *what for*. Never write "added for X" or "see PR #Y" — that is git's job.

Naming:

- `PascalCase` — classes, records, methods, properties, public fields, constants, enums.
- `camelCase` — locals, parameters.
- `_camelCase` — private fields.
- `IPascalCase` — interfaces.
- Async methods end with `Async`.
- Tests: `MethodUnderTest_Scenario_ExpectedOutcome`.

Forbidden in production code:

- `Thread.Sleep` in async paths — use `Task.Delay`.
- `.Result`, `.Wait()`, `.GetAwaiter().GetResult()` — deadlock risk.
- `catch (Exception) { }` — swallowing.
- `DateTime.Now` / `DateTime.UtcNow` directly — inject `TimeProvider`.
- `Guid.NewGuid()` in domain — inject an `IGuidProvider` so tests are deterministic.

Run `dotnet format` before every commit. CI fails on a non-empty diff.

## 5. Testing

Stack: xUnit, FluentAssertions, NSubstitute, Coverlet.

What we test:

- **Domain** — every behaviour, every invariant. Pure C#, no mocks. **Required.**
- **Application** — every use-case, ports stubbed via NSubstitute. **Required.**
- **Infrastructure** — integration tests where reasonable (yt-dlp wrapper against fixture URLs, EF Core mappings against in-memory SQLite). **Recommended.**
- **Host** — smoke test that the DI graph resolves. **Required.**

What we don't test:

- Private methods directly. Go through the public API.
- Framework internals (Serilog, EF Core themselves).
- Trivial getters / setters.

Conventions:

- AAA structure (Arrange / Act / Assert), with a blank line between sections.
- One *concept* per test (multiple assertions are fine if they verify one outcome).
- Test name: `Method_Scenario_Expectation`, e.g. `Extract_TikTokWatermarkedUrl_ReturnsCleanMp4`.
- `Theory` + `InlineData` for parametrised tests.
- Test fixtures (sample URLs, captured HTML) live in `tests/_fixtures/`.
- Never commit `[Fact(Skip = "...")]` without an issue link in the skip reason.

Coverage — Coverlet collects, ReportGenerator merges, CI gates per layer.

Two numbers per layer: the **target** we're climbing to, and the **floor** CI fails below today. The floor is a ratchet — raise it in the same PR that adds the tests clearing it; never lower it.

| Layer       | Target | Floor (CI fails below) |
|-------------|--------|------------------------|
| Domain      | 90 %   | 77 %                   |
| Application | 80 %   | 90 %                   |
| Overall     | 70 %   | 36 %                   |

Application already clears its target, so its floor guards the current line. Domain and overall trail — Infrastructure (only *recommended* above) is the gap. The gate is `tools/check-coverage.ps1`.

## 6. Commits — Conventional Commits

Format:

```
<type>(<scope>): <subject>

<body>

<footer>
```

- **Type:** `feat | fix | refactor | docs | test | chore | ci | perf | style | build`.
- **Scope (optional):** module name, e.g. `(extractor)`, `(tg-client)`, `(host)`.
- **Subject:** imperative ("add", not "added"); no trailing period; <= 72 chars; lowercase start.
- **Body:** wrap at 80; explain *why*, not *what*.
- **Footer:** `BREAKING CHANGE: ...`, `Closes #N`, `Refs #N`.

Examples:

```
feat(extractor): support youtube shorts urls

Adds a YouTubeShortsExtractor that handles both /shorts/<id> and the
canonical watch?v=<id>&shorts=1 forms. yt-dlp already supports these
but we needed a dedicated CanHandle predicate so the resolver picks
this strategy first.

Closes #14
```

```
fix(tg-client): retry SendVideo on 429 using server-advised delay
```

**Hard rules.**

- **Never** add `Co-Authored-By: Claude` or any AI attribution. The author is the human pushing the commit. Full stop.
- **Never** `--no-verify`. Fix the hook.
- **Never** `--amend` once pushed.
- **Never** mix concerns in one commit. Split.

## 7. Branches and Pull Requests

- `main` is always green and deployable.
- Short-lived branches: `feat/<kebab-name>`, `fix/<kebab-name>`, `chore/<kebab-name>`, `docs/<kebab-name>`.
- Open the PR early; draft is fine.
- **Squash-merge** to `main`; the squash subject must follow Conventional Commits.
- Delete the branch after merge.
- One PR = one logical change. If you cannot summarise it in a sentence, split it.

The PR description template lives in `.github/pull_request_template.md`.

## 8. Configuration and Secrets

- `appsettings.json` — defaults, committed.
- `appsettings.Development.json` — shared dev defaults; committed *only if it carries no secrets*.
- `appsettings.Local.json` — your personal local overrides; **gitignored**.
- Dev secrets — `dotnet user-secrets` (per-project, never on disk in the repo).
- Prod secrets — environment variables (`Telegram__BotToken`, etc.).

**Deployment identity is private.** The repo describes a *generic* Telegram link-forwarder bot. The actual bot handle (`@...Bot`), token, target group, and operator identity live only in `dotnet user-secrets` and local notes — never in `README.md`, `CLAUDE.md`, code comments, log messages, error strings, or commit messages. The source repo's own GitHub slug is *not* in that set — the CI badge in `README.md` points at the live workflow and names it; the bot's runtime identity above is the private part, not the code host.

Forbidden in the repo: `*.user`, `*.local.json`, `*.local.md`, `.env`, `secrets.json`, `appsettings.Production.json`, `docs/local/`.

If a secret slips into a commit: rotate it immediately at the source (`@BotFather` `/revoke` for a Telegram token), then rewrite history before pushing.

## 9. Logging — Serilog

Structured logging only. Never concatenate values into the template:

```csharp
log.LogInformation("Extracted {Platform} media in {ElapsedMs}ms", platform, sw.ElapsedMilliseconds); // good
log.LogInformation($"Extracted {platform} media in {sw.ElapsedMilliseconds}ms");                       // bad
```

- Sinks: Console in dev; Console + rolling file (JSON) in prod.
- Correlation: each update gets a `CorrelationId` (from `UpdateId`) pushed onto `LogContext` at the dispatcher boundary.
- Levels:
  - `Debug` — verbose dev info.
  - `Information` — lifecycle, request handled.
  - `Warning` — recoverable (retry, fallback).
  - `Error` — unhandled exception in a request.
  - `Critical` — bot unhealthy, restart needed.
- **No PII** at `Information` or above (user names, message text). PII at `Debug` is OK behind a flag.

## 10. Error Handling

- **Domain** uses `Result<T, DomainError>` (or `OneOf<T, DomainError>` — pick one in `Domain` and use it everywhere). No exceptions for control flow.
- **Infrastructure** throws typed exceptions: `MediaExtractionException`, `TelegramApiException`, etc. The composition root logs and recovers.
- **The bot must never crash on a single bad update.** Each update is processed inside a `try / catch` at the dispatcher boundary; the exception is logged with the correlation ID and the bot continues.
- Retry transient Telegram failures (429, 5xx) with exponential backoff via `Polly`.

## 11. CI — GitHub Actions

`.github/workflows/ci.yml` runs on every PR and on push to `main`:

1. Checkout.
2. Setup .NET 10.
3. `dotnet restore`.
4. `dotnet format --verify-no-changes`.
5. `dotnet build --no-restore -c Release`.
6. `dotnet test --no-build -c Release --collect:"XPlat Code Coverage"`.
7. Merge coverage with ReportGenerator; `tools/check-coverage.ps1` fails the build if any layer drops below its enforced floor (see §5). Upload the report.

A red CI = no merge. No exceptions.

## 12. Documentation

| File / folder            | Audience               | Purpose                                                         |
|--------------------------|------------------------|-----------------------------------------------------------------|
| `README.md`              | GitHub visitors        | What the bot does, screenshots, quick start, tech stack.        |
| `CLAUDE.md`              | Claude + maintainers   | This file. Rules of the road.                                   |
| `CONTRIBUTING.md`        | New contributors       | Lighter restatement; how to set up locally.                     |
| `docs/architecture.md`   | Maintainers            | High-level diagrams, why each layer exists.                     |
| `docs/decisions/`        | Maintainers            | ADRs — one .md per significant decision.                        |
| `docs/graphs/`           | Maintainers + Claude   | Knowledge graphs from `/graphify`.                              |

ADR format (`docs/decisions/NNNN-<slug>.md`):

```
# NNNN. <Title>

Date: YYYY-MM-DD
Status: Proposed | Accepted | Superseded by NNNN

## Context
## Decision
## Consequences
```

Write an ADR for any decision that's hard to reverse or surprising to a newcomer. Public APIs of `Domain` and `Application` get XML doc comments. Internal infrastructure does not unless behaviour is non-obvious.

## 13. Knowledge Graphs — `/graphify`

`graphify` builds an interactive knowledge graph from code. We keep a small history in `docs/graphs/`.

Run `/graphify` when:

- A feature of >= 200 LOC across multiple modules lands on `main`.
- About to refactor a non-trivial module — graphify it first to capture the "before" state.
- A new contributor (or future-you) needs to dive into an unfamiliar area.
- After a major architectural change (new layer, new platform, swapped DB).
- Monthly heartbeat on the whole `src/` to detect documentation drift.

Workflow:

1. `/graphify src/` (or a specific path).
2. Save the output to `docs/graphs/<YYYY-MM-DD>-<scope>.{html,json}`.
3. Add a one-liner to `docs/graphs/CHANGELOG.md`:
   ```
   - 2026-05-28 — full src/ — initial graph; baseline before P2.
   ```
4. Commit with `docs(graphs): <one-line summary>`.

Do not:

- Graphify every commit; signal-to-noise dies.
- Graphify before code exists; wait until there's structure to map.
- Treat the HTML as a substitute for `architecture.md` — graphs show *what is*; ADRs explain *why*.

## 14. Process Rules — for Claude Code specifically

When Claude is editing this repo:

- **Read `CLAUDE.md` first.** This file is the contract.
- **Small commits.** Stop at a coherent unit; do not snowball.
- **Run tests before saying "done."** `dotnet build` is not enough.
- **Never push to `main`.** Always a feature branch and PR.
- **Never `--no-verify` or `--force`** unless asked explicitly in the same turn.
- **No AI attribution in commits.** No `Co-Authored-By: Claude`, no `Generated with ...`, nothing of the kind. Ever.
- **Mirror the user's pacing.** If they ask for rules — write rules. Do not sprint into a full implementation unless asked.
- **Surface assumptions.** If a requirement is ambiguous and the answer changes the design, ask before committing to code.
- **Update `/graphify`** after a P1 milestone or before / after a substantial refactor.
- **Keep this file living.** When you learn something the rules should encode, update `CLAUDE.md` in the same PR.

---

*Last revised: 2026-05-28.*
