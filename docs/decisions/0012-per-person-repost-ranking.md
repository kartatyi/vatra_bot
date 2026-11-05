# 0012. Per-Person Repost Ranking

Date: 2026-10-15
Status: Proposed

## Context

A group wants to see who brings the most to the chat: who drops the links, and from where. The repost journal ([ADR 0004](0004-repost-journal-persistence.md)) was built to know nothing about people — a chat pseudonym, a URL, an outcome. A ranking can't be built on that, so the journal has to learn one more fact. The question is how little it can learn.

## Decision

### The journal records a sender id — on reposts only, and only for people

`RepostEvent.SenderId` is the Telegram user id of whoever posted the link. It is written only for the outcomes the ranking counts — a media repost or a text fallback — and only when the sender is a person. Bots (the anonymous-admin stand-in among them) and channel posts leave it null, and so does every row written before the column existed.

It is the raw id, not a `ChatHasher`-style pseudonym. The ranking has to ask Telegram who the person is, and a hash can't be asked. A user id is the same trust level as the chat id the logs already carry, and the journal file stays local-secret.

### Names are never stored

`/leaders` resolves each id through `getChatMember` when it runs. A renamed person shows their current name; someone who left the chat — or whom Telegram no longer knows — drops out of the ranking without anyone pruning a table. The reply writes names out rather than @-mentioning them, so a ranking pings nobody, and it uses no parse mode, so a name can't inject formatting.

### People can leave the ranking

`/leaders optout` records `(chat pseudonym, user id)` in `StatsOptOuts`; the ranking query excludes those ids, and `/leaders optin` deletes the row. Opting out is per chat and hides the person; it does not rewrite their journal rows, which still feed the anonymous `/stats` and `/top` numbers.

### What the ranking shows

Per chat, the last 30 days by default or all time with `/leaders all`: up to ten people by successful reposts, each with the platform most of their links came from. Hosts fold into platforms (`vt.tiktok.com` and `tiktok.com` are both TikTok) before the favourite is picked.

### Badges and `/me` (added 2026-10-15)

Three badges, each held by the one person leading its category in the window — a tie goes to whoever ranks higher, and a category nobody scores in awards nothing:

- ⚡ **first to bring it** — most links they posted first that *someone else* posted again later. Re-posting your own link is an echo, not a trend.
- 🦉 **night owl** — most reposts between 00:00 and 05:00 in `Stats:TimeZone` (Kyiv by default; UTC would put a Kyiv 01:00 at 22:00).
- 🥱 **repeat king** — most links posted that the chat had already seen.

`/me` shows one person their own card: links, place in the ranking, favourite platform, busiest local hour, and their ⚡/🥱 tallies. Someone who opted out gets a pointer to `/leaders optin` instead — opting out hides you from yourself too, rather than half-hiding you.

To count ⚡ and 🥱, the per-chat "seen links" table became `LinkSightings`: one row per time a chat saw a link, with who posted it and whether it was a repeat. **This widens what is recorded:** sightings carry a sender for every link the bot considered — including ones that weren't reposted, such as a news link it skipped. The link itself is still only the media cache's key digest, never a URL, and opt-outs are excluded in SQL from every badge query.

### A chat can say no to stats (added 2026-10-15)

`/settings` has a `📊 Stats` row, on by default. Off, the handler drops the sender before anything is recorded — the journal and the link sightings get no `SenderId` for that chat — and `/leaders`, `/me` and the digest's top droppers go quiet. Counts that identify nobody (`/stats`, `/top`) carry on. Turning it off hides and stops; it doesn't erase what was recorded before — that would be irreversible, and isn't what a toggle should do.

## Consequences

- The ranking starts empty: rows from before this change have no sender.
- The journal now holds a personal identifier. Exporting the database off the host now exposes who posted what, not just what was posted — the upgrade path in ADR 0004 (keyed HMAC) would have to cover `SenderId` too, and would cost `/leaders` its name lookup.
- `/leaders` costs one `getChatMember` call per listed person; at ten people that is well inside Telegram's limits.
- Failures carry no sender, so "whose links break most" is not answerable — deliberately.
