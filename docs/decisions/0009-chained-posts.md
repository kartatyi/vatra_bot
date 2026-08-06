# 0009. Reposting a Post the Author Wrote as a Chain

Date: 2026-08-06
Status: Accepted

## Context

Threads authors routinely split one thought across several messages — "1/3", "2/3", "3/3" — and the platform shows them as one post. The bot reposted only the first, so a chat reading the repost got the hook and none of the argument.

The parts are already in the payload [ADR 0008](0008-threads-post-payload.md) reads. The problem is that they sit in the same `edges` list as the comment section:

```text
edge 0  author       reply_to —          the linked post
edge 1  author       reply_to author     the continuation          ← wanted
edge 2  a stranger   reply_to author     a comment                 ← not wanted
edge 6  ukr9_s       reply_to author     a comment…
        author       reply_to ukr9_s     …and the author's answer   ← not wanted either
```

`thread_type` says `"thread"` for every one of them, so it can't be the discriminator.

## Decisions

### Decision 1 — A part continues the thread only if it is *by* the author and *to* the author

Flatten the conversation, find the linked post, then walk forward while each post satisfies both `user == author` and `reply_to_author == author`; the first that doesn't ends the chain. That second condition is what excludes the author's replies *inside* the comment section, which the "same author" test alone would have swept in.

### Decision 2 — Parts are their own messages, not more caption

`MediaPayload` gains `FollowUps`, a list of [`PostSegment`](../../src/LeBot.Domain/Media/PostSegment.cs) (text + its own media). They are deliberately *not* folded into `Description` and `Items`: each part is its own message on the platform, and Telegram's caption ceiling (1024) is a quarter of its text ceiling (4096) — the measured example is a 282-char post with 2011 chars of continuation, which no caption could hold.

`TelegramBotMessenger` sends consecutive text-only parts as **one** message (a "1/6" thread must not become six pings), splitting between parts when they exceed 4096. A part carrying its own photo or video has to be its own message, so it breaks the run and the text resumes after it.

### Decision 3 — The cache stores the whole thread, schema v2

A cached repost must replay what the first one delivered, so `entry.json` carries the parts and their media (`f00-00.jpg`, one prefix per part). The schema version goes to 2: a v1 entry cannot say whether its post continued, and serving half a thread is worse than re-extracting it.

## Consequences

**Positive**

- A chained post arrives whole, in order, in as few messages as the content allows.
- The rule is data-driven, not a heuristic on text ("1/3" numbering is a convention, not a field), so it works for authors who don't number their parts.

**Negative / open**

- Chains are capped at 10 parts; past that the repost stops being a courtesy. The cap is logged when it bites.
- Every existing cache entry is discarded on the first read after this ships — a one-off re-extraction of whatever is warm.
- The chain is only as complete as the page: Threads paginates very long conversations, and the bot does not follow that.
- If the linked URL points at the middle of a chain, the continuation starts from *there* — the earlier parts aren't retro-fetched. That matches what the link is asking for.
