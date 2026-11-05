# 0015. Telegram as the Media Store

Date: 2026-10-15
Status: Proposed

## Context

[ADR 0007](0007-media-cache.md) kept every repost on disk for 24 hours so a repeat link skips the platform. It works, but it costs a few gigabytes of disk, a copy of every file, and — the part a user notices — a full re-upload to Telegram on every hit: a 40 MB reel served "from cache" still takes as long to arrive as a fresh one.

Inline mode ([ADR 0013](0013-inline-mode.md)) already proved the alternative: once a file is uploaded to a private storage channel, Telegram hands back a file id that can be sent to any chat, instantly, with no upload at all.

## Decision

With `Storage:ChatId` set, the media cache is `TelegramMediaCache`, and the disk store is only the fallback for a deployment without a channel.

- **A fresh post is uploaded once — to the storage channel.** `TelegramMediaVault` sends the item (or the album, as one media group) there and reads back the file ids. The download is deleted straight away.
- **The repost itself goes out by file id.** `IMediaCache.SaveAsync` now returns the payload to send from then on, so the handler sends the stored post — no second upload. The disk store returns the caller's payload unchanged, so its behaviour is as before.
- **A repeat link is sent by file id** from a `StoredPosts` row: the post's text plus its items' file ids, as JSON, keyed by `MediaCacheKey` exactly like the disk store. Nothing is read from disk at all.
- **`MediaItem.TelegramFileId`** carries an item that Telegram already holds; its `FilePath` is empty. The messenger sends such items with `InputFile.FromFileId` and has nothing to delete.
- **Lifetime stays `MediaCache:TtlHours`** from the moment of storing. File ids outlive it, but the post's text may go stale, which is what the lifetime is about.
- **Inline mode shares the channel.** `Inline:StorageChatId` becomes `Storage:ChatId`; a post the cache already put in Telegram is offered inline without another upload.
- **Failures fall back, never fail the repost.** No channel, or an upload Telegram refuses: the caller's own payload comes back, the repost uploads directly as before, and that link simply isn't cached.

## Consequences

- No media on disk beyond the minutes between download and upload; the `cache/` directory empties itself as the old entries expire.
- A repeat link arrives in the time it takes to send a message, whatever its size.
- The storage channel keeps one message per stored post. The bot never deletes them: a file id is only guaranteed while its message exists.
- The bot now depends on Telegram keeping those files. If a file id ever stops resolving, that one send fails like any other Telegram refusal and falls back to the source link; the entry expires within `TtlHours`.
