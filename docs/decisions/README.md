# Architecture Decision Records

One `.md` per significant decision, numbered `NNNN-<slug>.md`.

**Write an ADR when** the decision is hard to reverse or would surprise a newcomer — swapping a core dependency (DB, LLM provider), adding or removing a layer, changing a public contract. Skip it for routine, easily-reversed choices.

## Template

```
# NNNN. <Title>

Date: YYYY-MM-DD
Status: Proposed | Accepted | Superseded by NNNN

## Context
<the forces at play — why a decision is needed now>

## Decision
<what we chose, and why over the alternatives>

## Consequences
<what gets easier, what gets harder, what's now locked in>
```

A multi-part ADR uses `## Decisions` with `### Decision N` subsections instead of a single `## Decision` (see [0001](0001-phase2-llm-provider.md)).

`Status` starts `Proposed` and flips to `Accepted` once the maintainer signs off; a later ADR can mark an old one `Superseded by NNNN`.
