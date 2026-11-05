# 0011. Per-Chat Caption Settings

Date: 2026-10-14
Status: Proposed

## Context

Every repost carried the post's full text as its caption. Under a TikTok or a Reel that text is mostly hashtags, @mentions and promo — a phone number, "follow for more" — and in a group chat the video already says what it has to say; the caption is noise people scroll past. Under a Threads or X post the opposite holds: the text *is* the post, and dropping it leaves an image with no point.

So one global switch can't be right, and neither can a hard-coded rule: some groups want the text under every video, others never. The choice belongs to each chat, and it has to survive restarts.

## Decision

### Two settings per chat, split by what the post leads with

`ChatSettings` (Domain) holds a `CaptionMode` — `Off` / `Brief` / `Full` — for **video-led** posts and one for **text-led** posts:

- **Video-led:** the payload carries a video and doesn't come from a text-first host. Defaults to `Off`.
- **Text-led:** everything else — Threads, X and Reddit even when they carry a video, photo posts, text-only replies. Defaults to `Full`. `Off` is not allowed here: a text-led post without its text has nothing worth reposting.

The split is by what the post leads with, not by platform alone, because one platform can be either: an Instagram reel is video-led, an Instagram photo post is text-led.

`Brief` strips hashtags and @mentions, collapses blank runs and cuts near 200 characters at a word boundary. `Off` drops title and author as well, so nothing falls back into the empty caption.

### Shaped on the way out, cached whole

`HandleIncomingMessageHandler` loads the chat's settings once per message and passes `settings.ShapeCaption(payload)` to the messenger. The media cache keeps the full payload, so one cached clip serves every chat under its own settings. Shaping is a payload transform, not a transport concern: the messenger only learns whether there is held-back text to offer.

### Held-back text is a tap away

Hiding text by default is only safe if the one post where it mattered — a recipe, a news quote — can still be read. So when shaping dropped or cut the text, `ShapeCaption` carries the original `Body` as `MediaPayload.RevealableText`, and the repost goes out with a "📝 Text" button. Pressing it swaps the full text into the same message and removes the button: no extra message in the chat. Anyone may press it — it only shows what the post already said.

The text waits in a `RevealableTexts` table keyed by chat pseudonym and message id. It lives 7 days, and every save sweeps what has expired, so no cleanup job is needed; a press after that answers "no longer available". An album gets no button — `sendMediaGroup` takes no `reply_markup` — which costs little, since albums are mostly photo posts and those keep their text anyway. The button is on by default and a third `/settings` row turns it off.

This table does hold post text, unlike the journal. It is the public text of the linked post, never a chat member's message, and it is gone within a week.

### Stored beside the journal, keyed by the chat pseudonym

A `ChatSettings` table in the same SQLite file as the repost journal, keyed by `ChatHasher.Of(chatId)` — the database still never holds a raw chat id. A chat with no row gets the defaults. Reads never throw: a locked or corrupt file degrades to the defaults and a warning, because a repost must not fail over its caption. Writes do throw, so an admin is never told a change was saved when it wasn't.

### Changed from Telegram, by admins only

`/settings` replies with an inline keyboard — one row per setting, the active choice ticked. A press is applied only when `getChatMember` says the presser is the chat's creator or an administrator; anyone in a private chat owns it. Every press is answered, since Telegram keeps the button spinning until it is. Callback data is parsed strictly: anything the keyboard didn't send — including a forged "text-led: off" — is ignored.

### A chat may opt out of silence

The bot's rule is to stay silent when a link yields nothing. A fourth `/settings` row lets a chat trade that for a 🤷 reaction on the link — off by default. It fires only when an extractor took the link on and failed, never for a link no extractor claimed: that is every news or GitHub link, and shrugging at those would be noise.

## Consequences

- Video reposts arrive bare by default. A group that wants the old behaviour sets video to `Full`.
- The bot now subscribes to `callback_query` updates; future inline buttons share this path and must use their own callback prefix.
- Every repost costs one indexed SQLite read for the settings, and a hidden caption one write.
- The text-first host list (`threads`, `x`/`twitter`, `reddit`) lives in Domain; a new text-first platform means adding it there.
- The keyboard copy is English, like the bot's other replies. Localizing it is a separate change.
