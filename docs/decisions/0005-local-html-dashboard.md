# 0005. Local HTML Dashboard: a Separate Read-Only Reader over the Journal

Date: 2026-06-28
Status: Proposed

## Context

The repost journal ([ADR 0004](0004-repost-journal-persistence.md)) and its read-side query API give us the data; the Telegram commands give a quick glance. But "which links broke", "did vX regress", and "outcomes over time" are easier to *read* as a page than as a chat message. We want an HTML dashboard.

Two constraints shape it. The bot is a **long-polling Worker** with no inbound network surface — and the **deployment identity is private** ([CLAUDE.md §Secrets](../../CLAUDE.md)): there is no public port to expose and no appetite for a Telegram Mini App that would route this through Telegram's servers. The operator already has an SSH tunnel to the box. So the dashboard's whole job is to render the on-box SQLite file to a browser reached *through that tunnel* — nothing more.

## Decisions

### Decision 1 — A separate read-only reader process, not an endpoint in the bot

The dashboard is its own project ([`LeBot.Dashboard`](../../src/LeBot.Dashboard)) — a minimal ASP.NET app that opens the *same* `data/lebot.db` and serves one page. The bot stays a clean Worker.

```text
bad   bolt ASP.NET + an inbound HTTP surface onto the long-polling bot
      → the bot now also listens on a socket; a web bug can take the reposter down
good  a separate reader over the same DB
      → failure isolation, least privilege (read-only), the bot keeps doing one thing
```

It composes only what a reader needs via [`AddRepostJournalReader`](../../src/LeBot.Infrastructure/DependencyInjection.cs) — the `DbContextFactory` and the query store, and *nothing else* (no Telegram client, no extractors, no hosted services). WAL (enabled by the bot) lets the reader read live while the bot writes.

### Decision 2 — Bind to loopback; the SSH tunnel is the perimeter

The reader listens on `127.0.0.1` only and ships with no authentication. That is deliberate: the only way in is the operator's existing SSH tunnel, so the host's own access control *is* the dashboard's.

```text
bad   bind 0.0.0.0 + bake in a login form     → a public port + home-grown auth to get wrong
good  bind 127.0.0.1, reach it over `ssh -L`   → no public port, no secrets, no new attack surface
```

`ssh -L 5005:127.0.0.1:5005 <host>`, then open `http://localhost:5005`. `--urls` can move the port but should stay on loopback.

### Decision 3 — Open the database read-only

The connection uses `Mode=ReadOnly`, so the dashboard process *cannot* mutate the bot's telemetry even by accident — the journal is the bot's to write, the dashboard's only to read.

```text
bad   a read-write connection "we promise to only SELECT"  → one stray write corrupts the source of truth
good  Mode=ReadOnly                                         → the OS enforces what the code intends
```

A missing or unreadable DB degrades to an empty page with a notice ("is the bot running?"), never a 500 — a diagnostic must stay up precisely when things are broken.

### Decision 4 — One self-contained page, no CDN

The UI is a single embedded HTML file with inline CSS/JS and **hand-rolled SVG** charts — no chart library, no web fonts, no CDN. The box may be headless and offline, and a dashboard that needs the public internet to render its own charts is no dashboard for a server.

```text
bad   <script src="https://cdn…/chart.js">  → blank charts on an offline/locked-down host
good  inline SVG, baked into the exe         → renders from the one binary, always
```

## Consequences

- **Easier:** the dashboard is "just a reader" — KPI cards, outcomes-over-time, per-platform reliability, the failing-links table, per-extractor breakdown, and regression-by-version are all `GET /api/data` over the existing query API. Operate it by running the exe beside the bot.
- **Locked in:** there are now two processes sharing one SQLite file. WAL makes that safe for one writer + readers; it is *not* a license for a second writer. The reader must stay read-only.
- **Harder:** one more thing to publish and launch on the box (covered in [deployment.md](../deployment.md)). The reader inherits the journal's schema — a migration the bot applies is one the reader simply reads.
- **Security:** correctness rests on the loopback bind + SSH tunnel. Exposing the port publicly would mean adding real auth first; that is out of scope by design.
