# Contributing

Thanks for the interest. This project takes code quality seriously — please read this guide and skim [`CLAUDE.md`](CLAUDE.md) for the full rule set.

## Setup

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download).
2. Clone and build:
   ```bash
   git clone https://github.com/<owner>/<repo>.git
   cd <repo>
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
- `dotnet format` is the source of truth — CI fails on a diff.
- See [`CLAUDE.md`](CLAUDE.md) section 4 for the complete list.

## Testing

- xUnit + FluentAssertions + NSubstitute.
- Unit tests for `Domain` and `Application` are required for new logic.
- Coverage thresholds are enforced in CI — see [`CLAUDE.md`](CLAUDE.md) section 5.

## Commits

- Conventional Commits, imperative mood, subject <= 72 chars.
- **No AI attribution.** No `Co-Authored-By: Claude`, no `Generated with ...`, nothing of the kind. Authorship belongs to the human pushing.
- No `--no-verify`. No `--force` on shared branches.

## Reporting Issues

Open a GitHub issue with:

- What you expected.
- What happened.
- Steps to reproduce (the Telegram URL or message, log excerpt with correlation ID).
- Bot version / commit hash.
