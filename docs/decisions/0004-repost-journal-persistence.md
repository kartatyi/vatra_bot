# 0004. Durable Repost Journal: EF Core + SQLite for the Dashboard

Date: 2026-06-28
Status: Proposed

## Context

The bot needs a dashboard — statistics, and above all *the links it breaks on* — to be useful to run. The only telemetry today is [`RepostMetrics`](../../src/LeBot.Application/Metrics/RepostMetrics.cs): in-memory counters that **reset on every restart** (and the bot self-updates, so restarts are routine). It can answer "how is this build doing right now" but not "which URLs failed last week", "did failures spike after v1.4", or "which platform is slowest" — none of which survive a process bounce, and none of which it ever recorded per-URL in the first place.

That means durable, queryable, per-event history. The stack table in [CLAUDE.md](../../CLAUDE.md) already names the persistence choice (EF Core · SQLite → PostgreSQL) but scoped it to Phase 2. The dashboard pulls that decision forward.

## Decisions

### Decision 1 — Persist now, as an append-only event journal

One row per URL the bot reaches a terminal state on — a [`RepostEvent`](../../src/LeBot.Application/Telemetry/RepostEvent.cs) carrying outcome, host, full URL, the error variant + reason on failure, elapsed ms, media count/bytes, the **build version**, and a hashed chat id. The journal sits *beside* `RepostMetrics`, not instead of it: counters stay the cheap "this build, right now" read; the journal is the durable history the dashboard queries.

```text
RepostMetrics   in-memory, resets on restart   → "/stats since boot"
RepostEvent     durable rows, survive restarts  → "links that broke last week", "regressions since vX"
```

Recording is **best-effort**: a journal write that fails (locked file, full disk) is logged and swallowed, never propagated — losing a telemetry row must not break the repost the user asked for.

```csharp
catch (Exception ex) when (ex is DbUpdateException or SqliteException)  // good — telemetry is auxiliary
    logger.LogWarning(ex, "Failed to journal repost event for {Host}", e.Host);
// bad — letting a telemetry write throw out of the handler and cost the user their video
```

### Decision 2 — EF Core + SQLite, with migrations (not `EnsureCreated`)

Honours the documented stack and keeps the SQLite → PostgreSQL path open (provider swap, not a rewrite). The schema *will* grow as the dashboard does, and the bot updates itself in the field — so `EnsureCreated` is the trap: it silently won't add a new column to an existing database, and the first write after a self-update throws `no such column` on a box no one is watching.

```text
EnsureCreated   new column → existing DB unchanged → "no such column" on the server   (bad)
Migrate()       new migration shipped → applied at startup → schema in step            (good)
```

Migrations are applied once at startup by [`RepostDatabaseInitializer`](../../src/LeBot.Infrastructure/Persistence/RepostDatabaseInitializer.cs), ahead of the poll loop. Generating them is pinned and reproducible via a local tool manifest (`.config/dotnet-tools.json`).

### Decision 3 — Telemetry in Application, mapping in Infrastructure

`RepostEvent`, the `IRepostEventStore` port, and the `RepostJournal` that stamps each event live in **Application** — mirroring where `RepostMetrics` already sits, and keeping the type free of any persistence concern. The EF mapping (table, shadow primary key, indexes, enum-as-text) lives in Infrastructure's [`LeBotDbContext`](../../src/LeBot.Infrastructure/Persistence/LeBotDbContext.cs) via the Fluent API — no `[Table]`/`[Key]` attributes leak inward. The singleton store opens a short-lived context per write through `IDbContextFactory`, the correct pattern for a long-lived consumer.

The database path is pinned beside the executable (`TelemetryOptions.ResolvedDatabasePath`), exactly as the logs and downloads are ([ADR 0003](0003-first-run-observability.md)) — the launch CWD stays irrelevant.

### Decision 4 — Store the URL, pseudonymise the chat

The dashboard's whole point is showing *which links break*, so the full URL is stored — consistent with the handler already logging URLs at Information. The **raw chat id is not**: the target group is private deployment identity ([CLAUDE.md §Secrets](../../CLAUDE.md)), so it's reduced to a stable [`ChatHasher`](../../src/LeBot.Application/Telemetry/ChatHasher.cs) digest — enough to count distinct chats and split stats per chat.

Be precise about what that buys, though. The digest is an **unkeyed, truncated SHA-256**, and a Telegram chat id is **low-entropy** (a `-100`-prefixed bounded integer), so this is *pseudonymisation / data-minimisation, not anonymity*: someone holding the database could confirm a **guessed** id by re-hashing it.

```text
good  the DB never holds the raw chat id in plaintext
bad   treating the digest as cryptographically irreversible — for a guessable id, it isn't
```

That's an accepted trade-off, not an oversight, because the bot **already logs the raw chat id at Information**, so the journal file is no more sensitive than the logs that sit beside it — both are local-secret, both gitignored, neither leaves the host. The keyed-HMAC upgrade (a per-deployment secret, never stored in the DB) is the right move **only if** the DB is ever exported off-box; until then it would be inconsistent gold-plating while the logs carry the id in the clear.

## Consequences

- **Easier:** any dashboard frontend (the planned Telegram commands and the local HTML view) is now just a *reader* over a clean schema; per-platform success rates, failure drill-downs, and "regression since release X" are all `GROUP BY`s, no time-series database needed at this volume.
- **Locked in:** EF Core migrations are now part of the contract — a schema change means `dotnet ef migrations add` (via the pinned tool) committed alongside the code, and the generated `Persistence/Migrations/` is marked `generated_code` so `dotnet format` leaves it alone.
- **Security:** EF Core 10.0.7 pulls `SQLitePCLRaw` 2.1.11 transitively, which carries a high-severity advisory (GHSA-2m69-gcr7-jv3q); the native bundle is pinned forward to the patched 3.0.x line.
- **Privacy (accepted limit):** the chat-id digest is pseudonymisation, not anonymity (Decision 4) — the journal is local-secret like the logs. Exporting the DB off-box is the trigger to switch `ChatHasher` to a keyed HMAC.
- **Harder:** the bot now owns a database file (`data/lebot.db`) — one more piece of runtime state to back up or reset, gitignored like the logs. Unbounded growth is a known follow-up (retention/pruning) the schema's `OccurredAt` index already anticipates.
