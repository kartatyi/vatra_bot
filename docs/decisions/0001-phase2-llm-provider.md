# 0001. Phase 2 — Conversational Layer Foundations

Date: 2026-05-28
Status: Proposed

## Context

Phase 1 is in main: the bot reposts media, falls back to text, retries flaky calls. Phase 2 wants something different — it should *talk*. From the user's framing:

- Build per-user dossiers from chat activity (who jokes about what, who posts which platforms, when they're online).
- Reply to mentions and DMs in character — Ukrainian-language, group-mate vibe, comfortable poking fun.
- Answer specific questions ("what did Petro post last week?") and run small tasks (reminders, group polls, lookups).

This ADR sets the foundation for that layer — provider, persistence, and the request-response contract — before we start typing the code. Everything below is **proposed**, not accepted; the user revises and the status flips before any of it lands.

The constraints from Phase 1 carry over:

- Clean Architecture stays. The LLM is an *adapter* behind a port in `Application`, not a leaky abstraction in `Domain`.
- Code/docs in English; the bot's reply text is in Ukrainian (the group's working language).
- No Co-Authored-By, no AI attribution in commits.
- Deployment identity (the bot handle and the chat name) stays out of the repo.

## Decisions

### Decision 1 — LLM provider: **Anthropic Claude (Sonnet 4.6 / Haiku 4.5)**

| | Anthropic Claude (chosen) | OpenAI GPT-4.x | Hybrid (route by task) |
|---|---|---|---|
| Personality fit (dry, Ukrainian humour) | Strong; Claude has good Ukrainian fluency and follows system-prompt persona consistently | Strong | Either |
| Tool use (for "remind me", polls, lookups) | Native via `tools` parameter, stable | Native via `functions`, stable | — |
| C# SDK | `Anthropic.SDK` (community) or raw HTTP; manageable | `OpenAI` package, official | Both |
| Cost model for chat-volume | Haiku 4.5 ~$1/$5 per Mtok in/out — well within hobby budget for a small group | GPT-4o-mini ~$0.15/$0.60 — cheaper | — |
| Long context for chat history | 200k tokens — plenty for dossier + recent chat | 128k | — |

**Rationale:** Both providers work. The deciding factor is that the user is already an Anthropic / Claude Code user (this very session). Reusing the same API key, the same dashboard, the same billing relationship reduces deployment friction. The cost gap matters at scale, not at "one group chat" volume.

**Routing within Anthropic:**
- Default chat-reply model: **Haiku 4.5** (fast, cheap, personality-capable).
- Dossier summarization / weekly recap: **Sonnet 4.6** (overkill for one-shot replies, right for longer reasoning passes).
- Escape hatch: a `LlmOptions.Model` config knob so we can switch models per environment without rebuilding.

### Decision 2 — Persistence: **EF Core + SQLite (dev) → PostgreSQL (when needed)**

Already pre-committed in CLAUDE.md. The Phase 2 work just makes it real:

- SQLite file at `data/lebot.db`, gitignored; survives bot restarts but not host loss. Good enough for a personal bot.
- Migrations checked in under `src/LeBot.Infrastructure/Persistence/Migrations/`.
- Swap to PostgreSQL when we have a real reason (multiple bot instances, off-host backups, real concurrency).

### Decision 3 — Dossier schema (first cut)

A dossier is one row per group member, with a JSON column for free-form notes plus typed fields for things we query on:

```
ChatMembers
  ChatId           bigint
  UserId           bigint
  Username         text
  FirstSeen        timestamptz
  LastSeen         timestamptz
  MessageCount     int
  Notes            jsonb           -- LLM-curated facts: interests, recurring jokes, etc.
  (PK: ChatId, UserId)

ChatMessages
  Id               bigserial
  ChatId           bigint
  MessageId        int
  UserId           bigint
  Timestamp        timestamptz
  Text             text            -- truncated; we don't need to store novels
  HadMedia         bool

ChatRecaps
  Id               bigserial
  ChatId           bigint
  PeriodStart      timestamptz
  PeriodEnd        timestamptz
  Summary          text            -- LLM-generated, used as compressed context
  (UNIQUE: ChatId, PeriodStart)
```

Recaps are how we keep context windows tractable. Instead of feeding the LLM 10,000 raw messages, we feed the last N recaps + the last ~50 raw messages. A background service writes a recap once per day per chat.

### Decision 4 — Request shape: **explicit triggers, never unsolicited**

The bot doesn't volunteer replies just because the chat is lively. It speaks when:

- It's mentioned (`@<bot> …`) or replied to.
- It's DM'd directly.
- A scheduled task fires (reminder, daily recap on demand).

This keeps the chat habitable and the LLM bill bounded. A toggle (`LlmOptions.ProactiveReplies`) can be flipped later if we want to experiment with the bot piping up on its own; default `false`.

### Decision 5 — Architecture extension

Two new components, no Phase 1 surgery:

```
src/
  LeBot.Application/
    UseCases/
      HandleMention/                # new use case parallel to HandleIncomingMessage
    Ports/
      IChatLlm.cs                   # SendAsync(prompt, history, tools, ct) → ChatResponse
      IChatStore.cs                 # dossier + message + recap reads/writes
  LeBot.Infrastructure/
    Llm/
      AnthropicChatLlm.cs           # adapter over Anthropic.SDK
    Persistence/
      LeBotDbContext.cs
      Repositories/...
```

`TelegramUpdateDispatcher` gains a branch: messages addressed to the bot route to `HandleMention`; everything else continues to `HandleIncomingMessage` as today. No change to Domain.

## Consequences

**Positive:**

- Phase 2 starts narrow: one provider, one DB, one new use case. Easy to iterate.
- Dossier + recaps give us a meaningful product feature (the user explicitly wants this) without unbounded context costs.
- Clean Architecture absorbs the change with two new ports and one new adapter — no churn in Domain or existing Application code.
- The user already has an Anthropic account, so onboarding is one API key.

**Negative / open:**

- Adding EF Core pulls a noticeable dependency tree into `Infrastructure`. We should commit to it once (not flip-flop to Dapper later).
- The LLM bill is bounded but not zero. A jail-broken loop (user spam-pinging the bot to rack costs) needs a rate-limit. Proposed: per-user `MaxMentionsPerHour = 30` in `LlmOptions`.
- Ukrainian fluency on edge cases (slang, code-switching with Russian) needs evaluation — the persona prompt will need iteration.
- Anthropic-lock-in: changing providers later requires a new adapter (~2-3 days), not a rewrite. Acceptable.

## Implementation order (proposed)

1. `IChatStore` + EF Core scaffold + SQLite migration. No LLM yet.
2. `TelegramUpdateDispatcher` captures every message into `ChatMessages` (silent build-up of corpus).
3. `IChatLlm` + `AnthropicChatLlm` adapter with a happy-path replier using a fixed persona prompt. No dossier, no tools yet.
4. `HandleMention` use case wires the above; mention/reply triggers a stub reply.
5. Persona prompt iteration with the user in the group until the tone feels right.
6. Dossier writeback: LLM emits "extract facts" on each mention, stored as `ChatMembers.Notes` JSON.
7. Daily recap background service. Recaps replace raw message context after N days.
8. Tools: `set_reminder`, `create_poll`, `lookup_post`. Add one at a time.

Each step is its own PR off `main`. Now that Phase 1 is stable, the CLAUDE.md §7 feature-branch rule applies in full.

---

*This ADR is proposed, not accepted. The user revises and flips the status before any code starts. Decisions 1, 2, and 4 are the most reversible if we change our minds; decision 3 (schema) is the one most worth pushing back on now, because changing it after we have data is the most painful.*
