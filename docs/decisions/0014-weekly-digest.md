# 0014. Weekly Digest

Date: 2026-10-15
Status: Proposed

## Context

Every other thing the bot says is an answer: to a link, to a command. A weekly summary of the chat's links — how many, from where, who brought the most — is the first message the bot would post unprompted. And a scheduled message needs to know where to go, while everything the bot stores identifies a chat only by its `ChatHasher` pseudonym, which can't be sent to.

## Decision

- **Opt-in per chat.** A fifth `/settings` row, `📬 Digest`, off by default; like every setting, only a chat's admins change it.
- **Monday, 10:00, the chat's local time** (`Stats:TimeZone`), covering the Monday-to-Sunday that just ended in that zone. `DigestSchedule` is pure and tested across the week boundary and the exact posting minute.
- **What it says:** links reposted and the change on the week before, the top platform (hosts folded into platforms), and up to three top droppers — named live through `getChatMember`, written out rather than @-mentioned, opt-outs left out. A week with no reposts posts nothing.
- **The raw chat id is stored only while the digest is on.** `ChatSettings.ChatId` is filled when an admin turns the digest on and cleared when they turn it off. The pseudonym stays the key; the raw id is a delivery address, kept no longer than it's needed.
- **No catch-up.** `WeeklyDigestService` sleeps until the next posting moment. A restart that straddles it skips that week: a missed summary is harmless, a duplicate is noise, and avoiding both would need a delivery log for little gain.

## Consequences

- The database now holds a raw chat id for every chat with the digest on — the same trust level as the logs, which already carry it.
- A chat that removed the bot keeps its row until someone turns the digest off; the send fails, is logged as a warning, and the other chats still get theirs.
- A deploy over Monday 10:00 costs that week's digest.
