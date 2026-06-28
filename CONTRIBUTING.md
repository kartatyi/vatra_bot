# Contributing

Thanks for the interest. This project takes code quality seriously — please read this guide and skim [`CLAUDE.md`](CLAUDE.md) for the full rule set.

## Setup

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download).
2. Clone and build:
   ```bash
   git clone https://github.com/kartatyi/vatra_bot.git
   cd vatra_bot
   pwsh tools/fetch-tools.ps1           # downloads yt-dlp.exe
   dotnet build
   ```
3. Configure your bot token via user-secrets:
   ```bash
   dotnet user-secrets init --project src/LeBot.Host
   dotnet user-secrets set "Telegram:BotToken" "<your-token>" --project src/LeBot.Host
   ```
4. `dotnet test` should pass.

## Workflow

1. Branch off `main`: `feat/<short-name>`, `fix/<short-name>`, `docs/<short-name>`, etc.
2. Make focused commits — Conventional Commits style (`feat:`, `fix:`, `docs:`, ...).
3. Run `dotnet format` and `dotnet test` before pushing.
4. Open a PR; the template walks you through the checklist.
5. Squash-merge once CI is green.

## Code Style

- File-scoped namespaces, nullable reference types on, `async` / `await` for every I/O path.
- Run `dotnet format` before committing; `.editorconfig` + analyzers are the source of truth.
- See [`CLAUDE.md`](CLAUDE.md) → **Code rules** for what tooling can't catch.

## Testing

- xUnit + FluentAssertions + NSubstitute.
- Unit tests for `Domain` and `Application` are required for new logic.
- Coverage is gated per layer in CI via [`tools/check-coverage.ps1`](tools/check-coverage.ps1) — a PR below the floor fails. See [`CLAUDE.md`](CLAUDE.md) → **Tests & CI** for the per-layer targets and enforced floors.

## Commits

- Conventional Commits, imperative mood, subject <= 72 chars.
- **No AI attribution.** No `Co-Authored-By: Claude`, no `Generated with ...`, nothing of the kind. Authorship belongs to the human pushing.
- No `--no-verify`. No `--force` on shared branches.

## Knowledge graphs (`/graphify`)

We keep a small history of source-tree knowledge graphs under [`docs/graphs/`](docs/graphs/).

Run `/graphify src/` (or a path) when a feature of ≥ 200 LOC across modules lands, before refactoring a non-trivial module, after a major architectural change, or as a monthly heartbeat — **not** on every commit. Then:

1. Save the output as `docs/graphs/<YYYY-MM-DD>-<scope>.{html,json}`.
2. Add a one-liner to `docs/graphs/CHANGELOG.md`.
3. Commit with `docs(graphs): <summary>`.

Graphs show *what is*; ADRs explain *why*; neither is a substitute for reading the code.

## Reporting Issues

Open a GitHub issue with:

- What you expected.
- What happened.
- Steps to reproduce (the Telegram URL or message, log excerpt with correlation ID).
- Bot version / commit hash.
