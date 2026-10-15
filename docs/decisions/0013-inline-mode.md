# 0013. Inline Mode Through a Storage Channel

Date: 2026-10-15
Status: Proposed

## Context

The bot only helps where it is a member. To send a friend a TikTok in a private chat, you still paste a link and they still leave Telegram. Telegram's inline mode — typing `@bot <link>` in any chat — would bring the bot's repost anywhere, but it comes with two hard constraints:

- **An inline result can only point at a file Telegram already holds** (a cached `file_id`), or at a public URL Telegram fetches itself. The bot downloads media to its own disk; nothing it has is public.
- **An answer must come fast.** The user is typing; a query unanswered within seconds is dropped, and every keystroke sends a new one. A first-time extraction can take far longer than that.

## Decision

### Upload to a private storage channel, remember the file id

A channel only the bot posts to, configured as `Inline:StorageChatId` (deployment identity — user-secrets, never committed). Preparing a link means: fetch it the way a group repost does (media cache, then the extractor chain), upload its first item to the channel, and store `(link key → kind, file_id, text)` in a `PreparedPosts` table keyed like the media cache. Every later query for that link — from anyone, in any chat — is answered from the table without touching the platform or re-uploading. Entries live 30 days, so an edited post isn't served stale for ever.

Only the first item goes up: Telegram has no inline albums. A multi-photo post offers its first photo with the text.

### Wait a little, then let the work finish in the background

An answer waits up to 8 seconds for the preparation. If it isn't done, the user is offered "⏳ Not ready yet — send the link instead", and the preparation keeps running on its own clock (3-minute cap). The next keystroke's query then finds it done. Concurrent queries for one link share a single preparation.

"Not ready" and "failed" answers both send the bare link, so choosing them never posts something odd — and in a chat where the bot lives, the link still gets reposted. Those answers are uncached by Telegram; ready answers may be cached for five minutes.

### Off the poll loop

The dispatcher handles updates one at a time. An inline answer that may wait 8 seconds runs detached, as its own error boundary, so it never stalls a group's reposts.

### Open to everyone

Anyone who knows the bot's name can use inline mode. That is the owner's call: the cost is server bandwidth for strangers' links, and the storage table makes every repeat link free.

## Consequences

- Inline mode needs two things outside the code: BotFather `/setinline`, and the storage channel with the bot as an admin. Without the channel id, only text posts can be answered.
- Nothing inline is journalled — there's no chat to attribute it to, and per-keystroke queries would drown the dashboard.
- A first-time long video usually answers "not ready" once. That's the price of Telegram's answer deadline.
- The storage channel grows by one message per prepared link. The bot never deletes them: a Telegram file id is only guaranteed while its message exists.
