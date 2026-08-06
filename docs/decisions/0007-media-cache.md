# 0007. On-Disk Media Cache with a 24-Hour Lifetime

Date: 2026-08-06
Status: Accepted

## Context

Links circulate in a group. The same TikTok gets posted in the morning and again that evening; someone quotes it back; a second chat picks it up. Every one of those costs a full extraction: a yt-dlp process spawn (or a headless Chrome launch for Threads), a download of bytes we already had, and one more request against a platform that counts them — Threads answers `429` to anonymous callers, and TikTok's JS challenge fails often enough that we retry it. The bytes were on disk minutes earlier and we deleted them right after sending.

Telegram's own `file_id` would make a re-send free, but it only covers media the bot itself uploaded, ties the cache to one bot token, and carries nothing about the payload's caption or author. Keeping the files is the more general answer, and the one that also serves the text-only fallback.

## Decision

A `IMediaCache` port in Application, implemented by `FileSystemMediaCache` in Infrastructure: one directory per URL under `MediaCache:Directory`, holding the media files plus an `entry.json` describing them. `HandleIncomingMessageHandler` asks it before running any extractor, and saves after a successful extraction. A hit skips the extraction chain entirely — no request leaves the machine.

### The key is the URL stripped to what selects content

Host lowercased and de-`www`'d, trailing slash trimmed, scheme and fragment dropped, and known share-tracking params removed (`_r`/`_t`/`is_from_webapp` from TikTok's share sheet, `igshid`, `s`/`t` from X, `utm_*`). Anything else — `?v=` on YouTube above all — is kept, because it might select the content. Without this the cache would almost never hit: two people sharing one clip produce URLs that differ only in the sharer's fingerprint. The normalized URL is stored in the entry and re-checked on read, so a hash collision can serve nothing.

### Entries live 24 hours, from write

Long enough to cover a link's circulation, short enough that an edited or deleted post isn't reposted stale for days. The lifetime does **not** slide on access: a popular link ages out on schedule rather than living forever. A `MediaCacheCleanupService` sweeps every 30 minutes so entries die on time on a quiet bot, and expired entries are also deleted the moment a read notices them. A `MaxTotalSizeMb` ceiling (2 GB default) evicts oldest-first, because a busy chat can outrun the clock.

### Ownership is explicit: `MediaPayload.RetainFiles`

The messenger deletes what it sends — that's what keeps the download directory clean. Cached payloads point at the cache's own copies, so the flag tells it to leave those alone. `SaveAsync` **copies** rather than moves: the caller still has to send and then delete the original, and a local copy is cheap next to a second extraction. The flag also makes `SaveAsync` a no-op for payloads that came out of the cache.

### Failures are never cached, and the cache never fails a repost

Only payloads with media or replyable text are stored; an extraction error is not. Caching a failure would turn a platform hiccup into a day of silence for that link. In the other direction every cache operation is best-effort — a corrupt entry, a missing file, an unreadable directory all degrade to a miss and a log line, never an exception into the handler.

## Consequences

**Positive**

- A repeat link is answered in the time of one Telegram upload: no yt-dlp process, no browser, no platform request — and no chance of being the request that trips a rate limit.
- The cache absorbs the flakiest paths we have (TikTok's challenge, Threads' `429`) for everything already seen once.
- `/stats` gained a "Served from cache" line; hits keep their original extractor attribution, so `ByExtractor` still reads true.

**Negative / open**

- **A post edited within 24 hours reposts as it was.** Deliberate — set `MediaCache:TtlHours` lower, or `Enabled: false`, if that ever matters more than the traffic saved.
- Up to `MaxTotalSizeMb` of disk sits idle beside the binary. It is swept, capped, and disposable — deleting the directory costs nothing but the next extraction.
- One extra local file copy per newly cached link.
- Only in-process state: entries survive a restart (they're files), but nothing coordinates two bot instances sharing a directory. Not a configuration we run.

---

*Accepted 2026-08-06. The load-bearing choices are the key normalization — without it the cache would barely hit in a group chat — and the fixed, non-sliding lifetime.*
