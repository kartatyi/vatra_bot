# 0008. A Public-Mirror Fallback for TikTok When yt-dlp's Challenge Solver Breaks

Date: 2026-08-11
Status: Accepted

## Context

TikTok gates its web pages behind an anti-bot challenge. yt-dlp solves it in `_solve_challenge_and_set_cookies`, and when TikTok changes the challenge, that solver breaks for *every* TikTok URL at once — not a class of posts, not login-gated content, everything. The failure is a flat `Unexpected response from webpage request`.

That is not hypothetical. On 2026-08-11 the outage was reproduced against the link a group member posted, against yt-dlp's own canonical test video, on stable and nightly builds alike, with browser cookies and without. The upstream ticket ([yt-dlp#17403](https://github.com/yt-dlp/yt-dlp/issues/17403)) was open with no fix, and the newest nightly build *predated* the outage — so `yt-dlp -U`, our standard answer to extractor rot, had nothing to offer. TikTok is the single most-posted platform in the chat, and the honest status was "silent until a stranger ships a patch."

Two self-hosted alternatives were tested and rejected. TikTok's `embed/v2` page returns HTTP 200 but no longer carries `playAddr` or the rehydration blob, so there is nothing to scrape. The official oEmbed endpoint works but yields only title, author, and a thumbnail — no video. Driving our existing headless-Chrome resolver (ADR 0006) at tiktok.com would walk straight into the same challenge that stops yt-dlp, which is precisely what the challenge is built to do.

## Decision

A `TikTokMirrorExtractor` in Infrastructure, implementing the existing `IPlatformExtractor` port, registered **after** `YtDlpPlatformExtractor`. It asks a public mirror API (tikwm by default) for the post's direct media URL and downloads it over plain HTTP.

### It is a fallback, not a replacement

`HandleIncomingMessageHandler` already walks every extractor whose `CanHandle` claims the URL and stops at the first that yields media. Registration order is therefore the whole mechanism: while yt-dlp works it wins and this class never executes. No feature flag flips when upstream heals — the mirror simply stops being reached. A mirror is the worse source (a third party, no cookie support, no format selection, no control over rendition quality) and should only ever earn its turn once the good source has failed.

### Endpoints are a configured list, not a constant

`TikTok:MirrorEndpoints` holds mirrors tried in order until one answers with media. A free public API will rate-limit, go down, or disappear; one hard-coded host would take the fallback with it. A mirror that answers "ok" but carries neither media nor a caption is treated as a failure so the next one still gets its turn.

### Both post shapes, one path

TikTok serves video posts and photo slideshows from the same URL. The mirror distinguishes them by an `images` array, so the extractor emits `MediaKind.Photo` items for a slideshow (capped at Telegram's 10-item album ceiling) and a single `MediaKind.Video` otherwise, preferring the HD rendition when the mirror offers one.

### The download is bounded the same way every other extractor's is

Same `YtDlp:MaxFileSizeMb` ceiling, same predictive Content-Length check plus a streaming cap for chunked responses, same `Result<>` error variants, same Polly retry shape as `InstagramApiExtractor`. Nothing about the failure path is novel.

## Consequences

**Positive**

- TikTok links keep working through an upstream outage instead of going silent, on the platform the chat posts most.
- Zero behaviour change while yt-dlp is healthy: the mirror is never called, so no third party learns anything.
- The seam generalises. Any platform whose primary extractor rots can get a fallback by registering one after yt-dlp.
- Slideshow posts, which yt-dlp handles poorly even when healthy, now repost as photo albums.

**Negative / open**

- **A third party sees the TikTok URLs the fallback handles.** Only the URL, only when yt-dlp has already failed, and it is a link already posted publicly — but it does leave the machine. `TikTok:MirrorFallbackEnabled: false` switches the whole path off and restores the previous silence.
- We do not control the mirror's uptime, rate limit, or rendition quality; the retry and the endpoint list soften that, they do not fix it.
- The mirror could in principle return something other than the requested post. The payload is not verified against the source URL beyond the mirror's own answer.
- One more registered extractor on every TikTok URL's chain, though it costs nothing until yt-dlp fails.
