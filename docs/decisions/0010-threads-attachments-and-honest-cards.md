# 0010. Threads: Attachments Are the Post's Media, and Unknown Shapes Fail Loudly

Date: 2026-09-03
Status: Accepted

## Context

Threads reposts broke four times in five weeks, each time reported from the group, each time a different post shape:

```text
2026-08-02  /share/ shortlinks          → not handled at all
2026-08-06  2-photo carousel            → the og:image collage
2026-08-06  a post written as a chain   → first message only
2026-09-03  a post sharing a reel       → the og:image collage, again
```

The last one is the one that explains the pattern. The post is `media_type 19` (text), caption "😍", with empty renditions on its own node; the reel — `video_versions`, poster, author — sits one level down under `text_post_app_info.linked_inline_media`. [ADR 0008](0008-threads-post-payload.md)'s parser reads three shapes (photo, video, carousel) off the node itself, saw nothing there, and did what its contract said: decline as `UnsupportedPlatform` so `ThreadsEmbedExtractor` could serve "a text-only post" with the card.

That is the mechanism that keeps the bug coming back, and it has two halves:

- **Every shape the parser doesn't know degrades to the card**, and the card is a *picture of the post* — author header, body text as pixels, a collage strip of whatever is attached. For text it is the post; for anything else it is wrong in a way that looks plausible.
- **The degradation is invisible.** The decline was logged at `Debug`, the card went out as a `MediaRepost` success, and the failure watchdog ([ADR 0008](0008-tiktok-mirror-fallback.md)'s sibling) triggers only on `Failure` rows. Nothing in the system could tell the difference between a text post and a post it had failed to read. Since the 08-06 fix, 11 of 35 Threads reposts went out via the card; nobody could say which of those were right.

```text
parser can't read the post → Unsupported → embed extractor → card → journal: MediaRepost ✓   bad — silent, plausible, wrong
parser can't read the post → Failure(ContentUnavailable: "attachment … linked_inline_media")  good — journaled, watchdog fires
```

## Decisions

### Decision 1 — What a post shares or quotes is the post's media

[`ThreadsPostPayload`](../../src/LeBot.Infrastructure/MediaExtraction/Threads/ThreadsPostPayload.cs) now reads, when the node's own renditions are empty, the nodes under `text_post_app_info`: `linked_inline_media` (a reel shared inline), `share_info.quoted_post`, `share_info.reposted_post`. They are full media nodes in the same Instagram shape, so the same reader applies; a quote of a quote is followed three levels deep and no further.

A quoted post's words go under the author's own — a quote without what it quotes reads as a non sequitur:

```text
this aged well

↩ @original.poster: bold prediction for 2026
```

### Decision 2 — The parser says what it couldn't read, and text-only means text-only

[`ThreadsPost`](../../src/LeBot.Infrastructure/MediaExtraction/Threads/ThreadsPost.cs) gains `UnreadAttachments`: when no media was read, the names of what the page still claims — `media_type 2` on a node with no renditions, `giphy_media_info`, an attachment whose own node claims media. `IsTextOnly` is true only when there is no media *and* nothing unread. A quoted text post stays text-only; a GIF post does not.

### Decision 3 — One Threads extractor; the card is a renderer, not a fallback

`ThreadsEmbedExtractor` is gone. What it did — fetch the page as `Twitterbot/1.0`, download `og:image` — lives on as [`ThreadsCardLoader`](../../src/LeBot.Infrastructure/MediaExtraction/Threads/ThreadsCardLoader.cs), a collaborator [`ThreadsPostExtractor`](../../src/LeBot.Infrastructure/MediaExtraction/Threads/ThreadsPostExtractor.cs) calls **only for a post the payload has shown to be text-only**. The card's honesty is decided by the one component that has the evidence, and nothing downstream can serve it without that evidence.

Everything else is a `Failure`, with `ContentUnavailable` naming the reason: an attachment the parser can't read (naming it, so the watchdog run knows which fixture to capture), a page that never described the post, a link that bounced off a post page onto the feed, media listed but not downloadable. A text-only post whose card won't load still goes out as its words — that is not a failure, the words are the post.

## Consequences

**Positive**

- A shared reel, a quoted photo, a reposted carousel go out as that media, with the author's caption.
- The next unknown shape produces a journal `Failure` naming it, the watchdog picks it up, and the fix is a fixture and a reader — not a group report three weeks later.
- The card can no longer be served for a post nobody parsed. Deleted and private posts, which used to go out as the feed's generic og:image, now fail cleanly.
- One Threads extractor, one decision point, and the chain order is no longer part of the correctness argument.

**Negative / open**

- Posts the parser can't read go silent in the chat until a reader exists — a GIF-picker post today. That is the trade this ADR makes: silence the watchdog can see over a plausible wrong picture nobody can.
- A logged-out 429 storm ([deployment notes](../deployment.md)) now shows up as `Failure` rows instead of junk cards. That is the truth, and the watchdog's per-signature run cap keeps it from spinning.
- `ThreadsEmbedExtractor` remains as a name in old journal rows and cache entries; nothing reads it back as a type.

---

*Supersedes Decision 3 of [ADR 0008](0008-threads-post-payload.md): the card is still reserved for text-only posts, but "text-only" is now proven from the payload rather than inferred from a decline.*
