# 0008. Threads Media from the Post's Own Payload

Date: 2026-08-06
Status: Accepted

## Context

Every Threads repost the bot has ever made was wrong, in one of two ways. Both were reported from the group, and both reproduce:

```text
2-photo carousel  → one image: Threads' social card — author header, body text as pixels,
                    a cropped strip of both photos glued side by side
photo + text post → a stranger's 4-second clip
```

Two sources, each broken in its own way:

- **`ThreadsEmbedExtractor` reads `og:image`.** That is not the post's photo; it is a *card Threads renders for crawlers*. For a carousel it is a collage. This is what the chat saw for essentially every Threads link — the journal shows `ThreadsEmbedExtractor` on all of them.
- **`ThreadsVideoExtractor` read `document.querySelector('video')`** in a headless page ([ADR 0006](0006-threads-headless-video.md)) — *the first video anywhere in the document*. A Threads page is not one post: the recommendation feed below it carries dozens. Measured on the reported post, the post's own photo sits at `top: 981px` and the `<video>` the resolver returned sits at `top: 12132px`, 4.06&#160;s long, belonging to someone else. On a photo post there is no post video to find, so the poll simply waited until a recommended clip rendered and shipped that.

ADR 0006's dead-end table concluded the media "appears nowhere in the 260&#160;KB page". That measurement is reproducible — with a bare `User-Agent`. It turns out Meta gates the server-rendered payload on the request *looking like a browser navigation*: add `Sec-Fetch-*`, `Upgrade-Insecure-Requests`, `sec-ch-ua*`, `Accept`, and the same URL comes back with the full Relay payload — `carousel_media`, `image_versions2`, `video_versions`, `caption.text` — the identical shape [`InstagramApiExtractor`](../../src/LeBot.Infrastructure/MediaExtraction/Instagram/InstagramApiExtractor.cs) already reads, because Threads runs on Instagram's backend.

```text
UA only                        → logged-out shell, no payload   (what ADR 0006 measured)
UA + full navigation headers   → payload present, 9 of 10 fetches
```

## Decisions

### Decision 1 — Read the payload the page ships with itself

[`ThreadsPostPayload`](../../src/LeBot.Infrastructure/MediaExtraction/Threads/ThreadsPostPayload.cs) scans the page's `<script type="application/json">` blocks and walks them for the node whose `code` equals **this post's shortcode**. That single constraint is the fix for the stranger's-video bug: the answer is defined by identity, not by document order. Media, caption, and author all come from the one node — so a carousel reposts as every photo, a video post as the clip, and the caption is the author's own text instead of "@user on Threads".

Verified against the two reported posts: the carousel now yields both photos, and the photo post yields its photo — no monkey.

### Decision 2 — Keep the headless browser, demoted to a fallback

One fetch in ten still comes back as the shell (Meta bounces it to the home feed with `?injected_media_ids=…`). When the payload is missing, [`ChromeDevToolsPayloadLoader`](../../src/LeBot.Infrastructure/MediaExtraction/Threads/ChromeDevToolsPayloadLoader.cs) — ADR 0006's CDP client, kept whole — loads the page in the system browser and returns the same payload block, parsed by the same code. A real browser is always served it.

The browser is still optional: no Chromium, `Threads:BrowserFallbackEnabled=false`, or any failure means that one post degrades to the og:image card. It is no longer on the path of the common case, so the 25&#160;s timeout a photo post used to burn (waiting for a `<video>` that would never come) is gone.

### Decision 3 — The og:image card stays, for text-only posts

`ThreadsPostExtractor` declines (`UnsupportedPlatform`) when the post has no media, so `ThreadsEmbedExtractor` still answers text-only posts. For those the card is not a lie — Threads renders the body text into it, which is exactly what the chat wants to see. Order: `InstagramApi → ThreadsPost → ThreadsEmbed → YtDlp`.

## Consequences

**Positive**

- Carousels repost as albums, videos as videos, captions as what the author wrote. The og:image card is now reserved for the one case where it is the right answer.
- The common case is one HTTP request — no browser launch, no 25&#160;s stall on photo posts.
- The parser is pure and fixture-tested, including a page whose recommendation feed offers a competing video.

**Negative / open**

- We depend on Meta continuing to server-render the payload to browser-shaped requests. When it stops, the browser fallback carries it; when both stop, the card is still there. Three levels, each degrading to today's behaviour.
- The navigation-header set is a fingerprint, and fingerprints rot. If Threads reposts regress to the card, that list is the first suspect.
- Login-gated posts stay out of reach — anonymous fetches see what a logged-out visitor sees, and Meta rate-limits them ([`threads 429`](../deployment.md)).
- Videos ship without a duration: Threads' payload carries none (`video_versions` has only `type` and `url`).

---

*Supersedes the mechanism of [ADR 0006](0006-threads-headless-video.md), not its reasoning about packaging: the CDP-over-WebSocket client and the "system browser, never shipped" stance are unchanged.*
