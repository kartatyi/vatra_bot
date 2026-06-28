# Telegram Link-Forwarder Bot

A Telegram group bot that re-posts the actual video / photo content from TikTok, Instagram Reels, YouTube Shorts, Threads, and other platforms — so the group conversation never has to leave Telegram.

[![CI](https://github.com/kartatyi/vatra_bot/actions/workflows/ci.yml/badge.svg)](https://github.com/kartatyi/vatra_bot/actions/workflows/ci.yml)
![Coverage](https://img.shields.io/badge/coverage-pending-lightgrey)
![License](https://img.shields.io/badge/license-MIT-blue)
![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)
![C#](https://img.shields.io/badge/C%23-14-239120)

## What it does

- Watches every message in the group, picks out `http(s)` URLs, and reposts whatever media those URLs point at as a native Telegram reply.
- Hybrid extraction chain: a custom Instagram embed scraper takes the first pass at `/p/...` posts (image carousels yt-dlp can't see), yt-dlp covers everything else (Reels, TikTok, YouTube Shorts, Threads, X, Reddit, Vimeo, Twitch, Facebook, and the rest of the ~1800 sites it supports).
- Re-posts as the right Telegram primitive: single `SendVideo` / `SendPhoto`, or `SendMediaGroup` for multi-image carousels, with the post's body text as the caption.
- Always replies with **something**. When a link is supported but media extraction fails, the bot falls back to the post's title/description as a text reply; when even that's empty, it acknowledges with "Couldn't extract media from this link."
- Polly retries transient Telegram failures (429, 5xx) with exponential backoff + jitter; permanent failures (400, 403) fall through to a source-URL text reply.

## Why

In a group chat, every external link is a context switch — open the app, watch the clip, come back, scroll up, momentum gone. The bot does the round trip so the chat stays in the chat.

## Roadmap

- [x] **Phase 1.** Link detection, media extraction, re-posting.
  - [x] Telegram update dispatcher with correlation IDs in the log scope
  - [x] yt-dlp wrapper via `YoutubeDLSharp`
  - [x] `IPlatformExtractor` strategy + hybrid Instagram embed scraper as the first extractor in the chain
  - [x] Re-post pipeline: single media, `SendMediaGroup` albums, text-only fallback, generic "couldn't extract" acknowledgement
  - [x] Polly retry on transient Telegram errors
  - [x] CI on GitHub Actions: format gate, build, test, per-layer coverage gate
- [ ] **Phase 2.** Conversational layer.
  - [ ] Per-user dossier storage (EF Core + SQLite -> PostgreSQL)
  - [ ] LLM-backed reply pipeline (provider TBD — see `docs/decisions/0001-phase2-llm-provider.md`)
  - [ ] Mention / DM routing
  - [ ] Lightweight task vocabulary (reminders, quick lookups, group polls)

## Tech Stack

- .NET 10 LTS, C# 14
- Clean Architecture (Domain / Application / Infrastructure / Host)
- `Telegram.Bot`, `YoutubeDLSharp`, `Serilog`, `Polly`
- xUnit + FluentAssertions + NSubstitute + Coverlet

A baseline knowledge graph of the source tree is checked in under [`docs/graphs/`](docs/graphs/) — open `2026-05-28-src.html` in any browser to navigate the architecture. The graph confirms the three god-nodes (`InstagramEmbedExtractor`, `YtDlpPlatformExtractor`, `TelegramBotMessenger`) and the Clean-Architecture domain isolation.

## Quick Start

> Steps 1–3 you can do right now in Telegram. Steps 4–5 start working once the first code commit lands.

### 1. Create a bot via @BotFather

In Telegram, open [@BotFather](https://t.me/BotFather):

1. `/newbot` → pick a display name → pick a username ending in `bot` (e.g. `your_name_bot`).
2. Save the **API token** BotFather returns. You'll need it in step 4. Treat it like a password — anyone with the token controls the bot.

### 2. Configure privacy and group access

By default a bot only sees messages addressed to it (mentions, replies, slash-commands). The link-forwarder needs *every* message, so:

1. `/setprivacy` → pick your bot → **Disable**. *(Critical. Without this the bot is blind to plain links.)*
2. `/setjoingroups` → pick your bot → **Enable**. Usually on by default.

Optional polish (any time):

- `/setdescription` — what people see in the bot's profile.
- `/setabouttext` — short bio.
- `/setuserpic` — avatar.
- `/setcommands` — slash-command menu (used in Phase 2).

### 3. Add the bot to your group

In the group: *Members → Add member → search the username → add*.

The bot does **not** need admin rights for Phase 1 — a regular member with group privacy disabled is enough. Promote to admin only if you later want it to pin or delete messages.

### 4. Clone and configure locally

```bash
git clone https://github.com/<owner>/<repo>.git
cd <repo>
pwsh tools/fetch-tools.ps1                                          # downloads yt-dlp.exe
dotnet user-secrets init --project src/LeBot.Host
dotnet user-secrets set "Telegram:BotToken" "<your-token>" --project src/LeBot.Host
dotnet run --project src/LeBot.Host
```

The token is stored encrypted under `%APPDATA%\Microsoft\UserSecrets\` on Windows (`~/.microsoft/usersecrets/` on Linux/macOS) — outside the repo. Never commit the token.

### 5. Verify

Post a TikTok / YouTube Shorts / Instagram Reels / Threads URL in the group. The bot should download the media and re-post it natively.

### Limits to keep in mind

- **File upload via the Bot API** caps at ~50 MB for videos and ~10 MB for photos. Larger media is dropped from the album and the source URL is sent as a fallback.
- **Rate limits:** Telegram allows ~30 messages/sec globally and ~1/sec per chat. Polly retries `429` / `5xx` with exponential backoff and jitter before giving up.
- **Group privacy must stay disabled.** Re-enabling it silently breaks link detection — the bot will keep running but never see plain-text messages.
- **Token rotation:** if a token ever leaks, revoke it via BotFather: `/mybots` → pick the bot → **API Token** → **Revoke current token**. Old token dies instantly.
- **Instagram image posts are best-effort.** Instagram closed off the public `/embed/captioned/` JSON during development; the embed scraper still tries first and produces a multi-photo album when IG hands data back, otherwise the bot falls through to a text reply built from the post's title and description (which yt-dlp gives us even when there's no video). See `docs/decisions/` or `memory` for the full background.
- **YouTube extraction may degrade without a JS runtime.** yt-dlp warns about missing `deno` for newer YouTube paths. Install `deno` (or `node`) and place it on `PATH` if you start seeing "Some formats may be missing" on Shorts.

## Contributing

See [`CONTRIBUTING.md`](CONTRIBUTING.md) for the short version and [`CLAUDE.md`](CLAUDE.md) for the full rule set.

## License

MIT — see [`LICENSE`](LICENSE).
