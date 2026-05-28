# Telegram Link-Forwarder Bot

A Telegram group bot that re-posts the actual video / photo content from TikTok, Instagram Reels, YouTube Shorts, Threads, and other platforms — so the group conversation never has to leave Telegram.

![CI](https://img.shields.io/badge/CI-pending-lightgrey)
![Coverage](https://img.shields.io/badge/coverage-pending-lightgrey)
![License](https://img.shields.io/badge/license-MIT-blue)
![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)
![C#](https://img.shields.io/badge/C%23-14-239120)

## What it does

- Detects media URLs in any group message — TikTok, Instagram Reels, YouTube Shorts, Threads, and the ~1800 sites supported by `yt-dlp`.
- Downloads the underlying media and reposts it as a native Telegram message.
- (Planned) Conversational layer with per-user dossiers, replies to mentions, and lightweight task running.

## Why

In a group chat, every external link is a context switch — open the app, watch the clip, come back, scroll up, momentum gone. The bot does the round trip so the chat stays in the chat.

## Roadmap

- [ ] **Phase 1.** Link detection, media extraction, re-posting.
  - [ ] Telegram update dispatcher with correlation IDs
  - [ ] yt-dlp wrapper via `YoutubeDLSharp`
  - [ ] Per-platform `IPlatformExtractor` strategy
  - [ ] Re-post pipeline with size-aware fallback (file vs. URL)
  - [ ] CI with format / build / test / coverage gates
- [ ] **Phase 2.** Conversational layer.
  - [ ] Per-user dossier storage (EF Core + SQLite -> PostgreSQL)
  - [ ] LLM-backed reply pipeline (provider TBD)
  - [ ] Mention / DM routing
  - [ ] Lightweight task vocabulary (reminders, quick lookups, group polls)

## Tech Stack

- .NET 10 LTS, C# 14
- Clean Architecture (Domain / Application / Infrastructure / Host)
- `Telegram.Bot`, `YoutubeDLSharp`, `Serilog`, `Polly`
- xUnit + FluentAssertions + NSubstitute + Coverlet

A high-level architecture diagram will land in [`docs/architecture.md`](docs/architecture.md) with the first feature.

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

- **File upload via the Bot API** caps at ~50 MB for videos and ~10 MB for photos. Larger media falls back to a clean direct URL.
- **Rate limits:** Telegram allows ~30 messages/sec globally and ~1/sec per chat. The bot retries with exponential backoff on `429 Too Many Requests`.
- **Group privacy must stay disabled.** Re-enabling it silently breaks link detection — the bot will keep running but never see plain-text messages.
- **Token rotation:** if a token ever leaks, revoke it via BotFather: `/mybots` → pick the bot → **API Token** → **Revoke current token**. Old token dies instantly.

## Contributing

See [`CONTRIBUTING.md`](CONTRIBUTING.md) for the short version and [`CLAUDE.md`](CLAUDE.md) for the full rule set.

## License

MIT — see [`LICENSE`](LICENSE).
