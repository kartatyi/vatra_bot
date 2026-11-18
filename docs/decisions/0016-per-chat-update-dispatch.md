# 0016. Per-Chat Update Dispatch

Date: 2026-11-18
Status: Proposed

## Context

The dispatcher handled updates strictly one at a time: `await` the handler, then take the next update. A single YouTube live-stream link made yt-dlp start recording a stream that never ends, and for two and a half hours no group got a repost, a command answer or a button press — every chat waited behind one link. Inline queries had already been moved off the loop for the same reason ([ADR 0013](0013-inline-mode.md)); everything else was still in the queue.

A timeout inside the extractor fixes that one cause. It doesn't fix the shape: any slow step — a 50 MB upload on a slow uplink, a platform that hangs, a lock — stalls every chat in the bot.

## Decisions

### Decision 1 — one lane per chat, many chats at once

`TelegramUpdateDispatcher` no longer awaits a handler. Each update is queued on a `ChatUpdateScheduler` under its chat: a message's chat, a button's message's chat, a membership change's chat; a button under an inline-sent message and a chosen inline result queue under the person who sent them. Within a key, updates run strictly in arrival order — a chained post's "2/3" never overtakes its "1/3", a `/settings` press never races the message it answers. Different keys run side by side.

Per-chat order rather than none at all, because the handlers assume it: "has this chat seen this link?" is a read-then-write in `SqliteSeenLinkStore`, the settings keyboard reads then saves, and a "show text" press takes a row once. Keyed by chat, those stay single-writer without a lock in any store.

Inline queries and placeholder completions stay detached, as in ADR 0013: they belong to no chat, and a query must not wait behind the previous keystroke's preparation.

### Decision 2 — a global cap, `Telegram:MaxConcurrentUpdates` (default 8)

At most that many updates run at once. An update takes a slot only once its chat's previous update has finished, so a chat with work piled up behind a stuck update holds one slot, never more — a stuck or chatty chat can't crowd the rest out. What waits costs memory, not a slot: running work is bounded, the queue is not, and at a group chat's pace it never needs to be.

Eight, because YoutubeDLSharp already runs at most four yt-dlp processes at once: a cap of four would let four slow downloads block a cache hit or a `/ping`; much more than eight buys nothing on one home box where each slot may be downloading, transcoding and uploading 50 MB.

### Decision 3 — acknowledge on hand-off, not on completion

The offset passed to `getUpdates` moves past an update as soon as it is queued. The alternative — hold it at the oldest update still running — doesn't survive Telegram's API: `getUpdates` returns the oldest unacknowledged updates, at most a hundred, so once a hundred updates piled up behind a stuck one the loop would see nothing new — the original freeze, delayed. A restart would also replay every chat's already-handled updates behind it as duplicate reposts.

So delivery is at most once: an update still running when the process dies is not delivered again, and whoever posted the link posts it again. The old loop delivered at least once, but at the price of re-running the whole last batch — handled updates included — after every restart.

### Decision 4 — shutdown cancels, drains, and gives up

On shutdown the poll loop stops, every handler sees the cancelled token, queued updates that hadn't started are dropped, and the dispatcher waits up to 10 s for the running ones before logging a warning and returning — inside the host's 30 s shutdown budget.

### Decision 5 — no update runs for ever, `Telegram:UpdateTimeLimitSeconds` (default 600)

An update still running after ten minutes is cancelled; one that ignores the cancellation and is still going 30 s later is abandoned — left running on its own while its chat's next update goes ahead. Ten minutes sits past the slowest honest repost and past yt-dlp's own five-minute download limit, so it only fires on a hang nothing below it caught. Without it a stuck update would hold its chat, and its slot, for ever.

## Consequences

- One slow link delays only its own chat; the poll loop never waits on a handler.
- Handlers now run concurrently, so every singleton they share must be safe under concurrent calls. Today's are: stores open a `DbContext` per call, SQLite queues writers on its lock (busy timeout 30 s), metrics are `Interlocked`. The disk media cache had to learn that a save in progress isn't a crash leftover.
- The same link posted in two chats within seconds is now extracted twice at once, as a group post and an inline query for it already could be. yt-dlp used to name its downloads `title [id].ext`, so the two runs shared one file and the first to finish deleted it under the second; each run now adds a GUID to the name, as the other extractors already did.
- Anything stuck holds one slot until a timeout below it fires, at the latest the update's own ten-minute limit. An abandoned handler keeps running without a slot, so a run of hangs that ignore cancellation can briefly put more than eight updates to work.
- Unhandled-update loss on a crash is now possible by design, and documented on the dispatcher.
