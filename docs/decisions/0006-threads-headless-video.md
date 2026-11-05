# 0006. Threads Video via a Headless Browser (CDP)

Date: 2026-06-29
Status: Superseded by 0008

> **Superseded on 2026-08-06 by [ADR 0008](0008-threads-post-payload.md).** Reading the first
> `<video>` in the page turned out to repost media belonging to *other* posts on it, and the media
> the "dead ends" table below declared unreachable is in fact server-rendered — to requests carrying
> a full set of browser navigation headers, which that investigation didn't send. The CDP client and
> the system-browser packaging stance survive intact; only what we ask the page for changed.

## Context

Threads video posts re-posted as a **still image**, not the clip. `ThreadsEmbedExtractor` only reads the crawler-visible `og:image` (a poster frame); it never had a video path. Closing that gap turned out to be the whole story — every cheaper route is a dead end, verified empirically against a live video post:

- **`og:video` does not exist.** Threads serves crawlers (Twitterbot/facebookexternalhit) only `og:image`.
- **No video URL in the HTML at all** — logged out *or* logged in. `video_versions` / `playable_url` / `.mp4` appear nowhere in the 260&#160;KB page. Threads fetches its media client-side via a GraphQL POST *after* the page's JavaScript runs.
- **yt-dlp has no Threads extractor** (checked stable `2026.06.09` and nightly `2026.06.27`). `threads.net` 302-redirects to `threads.com`, which the generic extractor can't resolve.
- **The Instagram private API doesn't know the post** — a Threads shortcode decodes to a PK that `instagram.com/api/v1/media/{pk}/info/` answers `Media not found`; Threads media live in a separate ID space.
- **The GraphQL route needs a persisted-query `doc_id`** that is lazy-loaded behind Meta's bootloader (`cr:` module, not in the initial bundles) and **rotates**. Reproducing it without executing the page JS is brittle reverse-engineering — exactly the "whitelist that goes stale" the codebase avoids ([`YtDlpPlatformExtractor.CanHandle`](../../src/LeBot.Infrastructure/MediaExtraction/YtDlp/YtDlpPlatformExtractor.cs)).

What *does* work: load the post in a real browser and read the rendered `<video>`. The element's `currentSrc` is a direct **progressive MP4** on the fbcdn CDN — downloadable over plain HTTP, no login required. Proven end-to-end (HTTP 200, `video/mp4`, valid `ftyp`).

## Decisions

### Decision 1 — Headless browser, accepted as a runtime dependency

Let the page do the work we can't reproduce. A `ThreadsVideoExtractor` drives a headless browser to the post, reads the first `<video>`'s source, and downloads it. This is the only path that survives Meta's client-side rendering and `doc_id` rotation, because the browser fetches the current `doc_id` itself.

Rejected: hardcoded/runtime-scraped `doc_id` (brittle, rotates, can't be obtained without JS) and a third-party embed-fixer (dead services, privacy leak).

### Decision 2 — Talk CDP directly, not Playwright

We speak the **Chrome DevTools Protocol** over a WebSocket (`System.Net.WebSockets`, built-in) and launch the **system browser** — no Playwright/Selenium package, no bundled Chromium.

The deploy model forced this: the bot ships as a **single-file, self-contained exe** (ADR 0002). Playwright's Node driver is *not* found under single-file publish (`Driver not found: …\.playwright\node\…`), and `PLAYWRIGHT_DRIVER_PATH` didn't fix it. Hand-rolled CDP needs nothing on disk but `chrome.exe` — verified working from a single-file publish run from an arbitrary working directory. ~200 lines of our code beats a fragile driver-packaging step.

### Decision 3 — System Chrome → Edge, browser is a prerequisite not shipped

`ChromeDevToolsVideoResolver` auto-detects Chrome, then Edge (always present on Windows 11), with a `Threads:BrowserPath` override. When **no browser is found, or `Threads:VideoExtractionEnabled=false`, or anything fails/times out**, the resolver returns `null` and `ThreadsVideoExtractor` declines (`UnsupportedPlatform`) — so `ThreadsEmbedExtractor` still serves the og:image. **Never worse than before.** A host without a Chromium browser simply keeps today's thumbnail behaviour.

### Decision 4 — Order: video extractor before embed extractor

Registered `InstagramApi → ThreadsVideo → ThreadsEmbed → YtDlp`. A video post yields the clip; a photo/text post carries no `<video>`, so `ThreadsVideoExtractor` declines and the chain falls through to the embed thumbnail. One headless launch is gated by a `SemaphoreSlim(1)` so concurrent links don't fan out browsers.

## Consequences

**Positive**

- Threads video posts finally re-post as video, login-free, with a clean fall-back to the old behaviour on every failure mode.
- Zero new NuGet/native packaging; single-file deploy is untouched. The browser interaction sits behind `IBrowserVideoResolver`, so `ThreadsVideoExtractor` is unit-tested with a fake.

**Negative / open**

- **Chrome/Edge is now a runtime prerequisite** for Threads *video* (only). Documented in `docs/deployment.md`; absence degrades to the thumbnail, it doesn't break.
- A headless launch per Threads-video link costs ~3–6&#160;s and a Chrome process. Acceptable for an occasional case, serialized to one at a time.
- **v1 carries no caption/author** on the video (Threads' `og:title` is just "@user on Threads" anyway). A later pass can grab it in the same CDP session.
- v1 takes the **first** `<video>` only — a multi-video Threads post yields its first clip.
- CDP is a moving protocol, but the surface we use (`Target.createTarget` / `attachToTarget` / `Page.navigate` / `Runtime.evaluate`) is stable and version-agnostic.

---

*Accepted 2026-06-29. The empirical dead-end table above is the load-bearing part: it's why a headless browser — heavier than anything else in the extraction path — is the right call and not over-engineering.*
