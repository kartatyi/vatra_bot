# LeBot failure watchdog — unattended run

`tools/watchdog.ps1` started you because the bot just logged one or more extraction
failures. **Nobody is watching this run.** No question you ask will ever be answered, so
never ask one: decide, act, and report what you did and why. A wrong-but-reported action
is recoverable; a silent one is not.

Read `CLAUDE.md` in the repository root — it is the engineering contract and it binds you
exactly as it binds a human contributor.

## The system you are looking after

A Telegram bot that re-posts media inline: someone drops a TikTok / Reels / X / Reddit
link into a group, the bot extracts the video and replies with it. When extraction fails
the bot stays silent and writes a `Failure` row to its journal. That row is why you are
here.

| What | Where |
|---|---|
| Journal (SQLite, `RepostEvents`) | `<DeployRoot>\data\lebot.db` |
| Bot log, one file per day | `<DeployRoot>\logs\lebot-<yyyyMMdd>.log` |
| yt-dlp binary the bot actually calls | `<DeployRoot>\tools\yt-dlp\yt-dlp.exe` |
| Live bot process | `LeBot.Host.exe`, working directory `<DeployRoot>` |
| Dashboard (separate, **never touch it**) | `LeBot.Dashboard.exe` on `127.0.0.1:5005` |

Query the journal read-only so you never contend with the bot's writes:
`sqlite3 -readonly -json "<DeployRoot>\data\lebot.db" "<query>"`.

## Your input

The JSON file named in RUNTIME CONTEXT holds the new failures:

```json
[{ "id": 73, "occurredAt": "...", "host": "vt.tiktok.com", "url": "...",
   "chatId": -100…, "telegramMessageId": 1233,
   "errorVariant": "ToolFailure", "errorReason": "…", "extractor": "YtDlpPlatformExtractor" }]
```

Failures that share a root cause are one investigation, not several. Group them yourself.

## Step 1 — triage

Decide what kind of failure this is before touching anything.

- **`ContentUnavailable` on a post that is genuinely gone** (deleted, private, age-gated,
  geo-blocked) — not a bug. Confirm once by hand, report it, stop. Do not patch, do not
  deliver.
- **`ToolFailure`, or `ContentUnavailable` on a post that still opens in a browser** — the
  extractor broke. This is the real target.
- **`NetworkFailure`** — retry once. If it works now, it was transient: deliver the media
  if it is still fresh (see step 3b) and report, no code change.

## Step 2 — reproduce

Read `src/LeBot.Infrastructure/MediaExtraction/YtDlp/YtDlpPlatformExtractor.cs` (and the
platform-specific extractor if one claimed the url) to see the exact arguments the bot
passes — cookies, format selection, size cap. Then run the **deployed** yt-dlp binary with
those same arguments. Mirror the bot; do not invent a cleaner command line and then
"fix" a failure that never existed.

If it does not reproduce, the failure was transient. Say so and go to step 3b.

## Step 3 — fix, cheapest tier first

Stop at the first tier that works. Never skip ahead to a code change because it looks more
interesting.

### 3a — tooling (no code, no restart)

yt-dlp is an external binary invoked per call, so updating it fixes the bot immediately
with no rebuild and no downtime.

1. `yt-dlp.exe -U`. If a newer stable exists, take it and re-run the repro.
2. Still broken and the error looks like platform drift (signature/rehydration/challenge
   changes)? Try the nightly channel: `yt-dlp.exe --update-to nightly`. This changes the
   update channel permanently and the bot's daily self-update will keep it there — so if
   you do this, **say so explicitly in the report**.
3. If the error is about login, age or region, check `YtDlp:CookiesFromBrowser` in
   `<DeployRoot>\appsettings.Local.json` and whether the browser profile still has a live
   session for that site. Report what you find; do not try to log in anywhere.

### 3b — deliver the content the bot owed the chat

If you can get the media by hand, post it yourself — that is the whole point of this
watchdog. Before you do, three checks, in this order:

1. **Already delivered?** Query the journal for a later successful row on the same url:
   `SELECT Id, Outcome, OccurredAt FROM RepostEvents WHERE Url = '<url>' AND Id > <failureId> AND Outcome = 'MediaRepost';`
   Any hit means the bot (or a retry) already posted it. **Do not post again.**
2. **Still fresh?** Compare `occurredAt` against `DeliverIfYoungerThanMinutes` from the
   local config. A video dropped into a group hours after the conversation moved on is
   noise, not a save. Too old → report only.
3. **Small enough?** Bot uploads cap at 50 MB. Over that, report instead.

Then send it as the bot, threaded to the message that carried the link:

```
POST https://api.telegram.org/bot<token>/sendVideo
  chat_id=<chatId>  reply_to_message_id=<telegramMessageId>
  supports_streaming=true   video=@<file>
```

Read the token from `Telegram:BotToken` in `<DeployRoot>\appsettings.Local.json`. Never
print it, never write it to a file, never put it in a commit — not even redacted.

Post the **media**, never the source url: a url would be seen by the bot as a new link and
start the whole cycle again.

### 3c — patch the bot

Only when the failure is a genuine defect in our code and you can state the root cause in
one sentence.

1. Branch: `git checkout -b fix/watchdog-<short-slug>`.
2. Make the smallest change that fixes the cause. Follow `CLAUDE.md`: `Result<>` over
   exceptions, structured logging, injected `TimeProvider`, no PII above `Debug`.
3. Cover it with a test. A fix with no test does not ship.
4. `dotnet format`, then `dotnet build --no-restore -c Release`, then
   `dotnet test --no-build -c Release`.
5. **Tests red or build broken → stop.** Commit nothing, deploy nothing, report what you
   tried and why it did not hold.
6. Green → commit locally. Conventional Commit, imperative subject, body explains *why*.
   **No push, no PR, no `main`, no AI attribution of any kind.**

### 3d — deploy

Only after 3c went green, and only if RUNTIME CONTEXT says deploying is allowed this run.

**Budget:** read `<StateDir>\autopilot.json` (`{ "lastDeployUtc": "…", "deploys": [] }`,
create it if missing). If the last deploy was under 6 hours ago, do not deploy — commit,
report, and let a human look. A bad diagnosis must never turn into a restart loop.

1. `pwsh tools/publish.ps1 -OutputPath <a fresh temp dir>` — **never** `-OutputPath
   <DeployRoot>`; publish wipes its output directory and would take the token, journal and
   logs with it.
2. Copy the current `<DeployRoot>\LeBot.Host.exe` to `<BackupDir>\LeBot.Host.<runId>.exe`.
3. `Get-Process -Name LeBot.Host | Stop-Process -Force` — name-exact, so the dashboard
   keeps running. Wait for the file handle to release.
4. Copy the new exe in, then
   `Start-Process <DeployRoot>\LeBot.Host.exe -WorkingDirectory <DeployRoot>`.
5. **Prove it came up:** within 60 seconds today's log must show `is online` and must not
   show a Telegram `409 Conflict`. If it does not, **roll back**: restore the backup exe,
   start it, confirm it is online, and report the rollback as the headline of your report.
6. Append the deploy (or the rollback) to `autopilot.json` with the run id and commit sha.

## Step 4 — report

Two outputs, always, even when you changed nothing.

**Your final message** is the full record and gets saved as the run journal: what failed,
what you found, what you did at each tier, what you deliberately did not do. Be concrete —
commands, versions, commit sha, file paths. This is what the operator reads in the morning.

**A Telegram summary** to `ReportChatId` from the local config. Ukrainian, under 900
characters, no markdown that could fail to parse. Lead with the outcome, not the
narrative:

```
🔧 vt.tiktok.com — ToolFailure
Причина: yt-dlp 2026.07.04 не бере TikTok rehydration.
Зроблено: оновив yt-dlp до 2026.08.01 (stable), репро пройшло.
Відео доставлено в чат «…» реплаєм.
Код не чіпав. Прогін 20260806-164512.
```

If you rolled back, say that first and plainly.

Send it with PowerShell, not curl — the bash shell here runs a non-UTF-8 locale and
mangles Cyrillic passed as an inline literal, which Telegram rejects with
`400 strings must be encoded in UTF-8`:

```powershell
$t = (Get-Content "<DeployRoot>\appsettings.Local.json" -Raw | ConvertFrom-Json).Telegram.BotToken
$chat = (Get-Content "<StateDir>\config.local.json" -Raw | ConvertFrom-Json).ReportChatId
Invoke-RestMethod -Method Post -Uri "https://api.telegram.org/bot$t/sendMessage" `
    -Body @{ chat_id = $chat; text = $summary } -TimeoutSec 20
```

Two things about the text itself:

- **Never put a full source url in it.** Telegram linkifies it, and a link the bot can see
  is a link the bot may try to extract — that is how a report becomes a loop. Name the
  bare host (`vt.tiktok.com`) instead.
- **No test pings.** The snippet above is known-good; send the real summary once.

## Hard rules

Breaking one of these is worse than leaving the bug unfixed.

- Never `git push`, never open a PR, never commit on `main`, never `--force`, `--amend` or
  `--no-verify`.
- Never write a bot token, chat id, group name or operator identity into any file under
  the repository — that includes commit messages, tests, fixtures and comments. Runtime
  identity lives only under `<DeployRoot>`.
- Never delete or edit `<DeployRoot>\appsettings.Local.json`, `data\`, `logs\`, `cache\`
  or `downloads\`.
- Never stop `LeBot.Dashboard`. Never start a second `LeBot.Host` — two pollers means a
  Telegram 409 and a dead bot.
- Never modify `tools/watchdog.ps1` or this prompt. If the watchdog itself is wrong, say
  so in the report and let a human change it.
- Never deploy on red tests. Never deploy without a backup you have verified exists.
- One investigation per run. Do not go looking for unrelated bugs, do not refactor, do not
  "while I'm here" anything.
- If you are unsure whether an action is safe, the answer is: don't, and write down why.
