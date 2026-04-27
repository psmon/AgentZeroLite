---
name: agent-origin-reference
type: reference
owners: [tamer, code-coach]
related_docs:
  - Docs/agent-origin/README.md
  - Docs/agent-origin/01-stack-comparison.md
  - Docs/agent-origin/02-architecture-comparison.md
  - Docs/agent-origin/03-adoption-recommendations.md
---

# Agent Origin (AgentWin) Reference Protocol

## Purpose

AgentZeroLite is a fork of the **AgentWin** project (`D:\Code\AI\AgentWin`).
Origin diverged into a desktop-hub product; Lite kept the lightweight multi-shell
identity. Both codebases evolve in parallel — when comparing or borrowing
patterns, do **not** re-survey Origin from scratch. A maintained snapshot lives
in `Docs/agent-origin/`.

## When to consult `Docs/agent-origin/`

Trigger on any of:

- Korean: "오리진", "조상 프로젝트", "AgentWin", "오리진이랑 비교", "오리진 참고", "오리진 가져와", "원본"
- English: "the origin", "the ancestor", "AgentWin", "compare with origin"
- Implicit signals: user asks "is there a better pattern", "did we have this
  before", "what did AgentWin do here"

## Lookup order

1. **Start with** `Docs/agent-origin/README.md` — Executive summary + P0~P3
   adoption table. Most "what does Origin have that we don't" questions are
   answered here in one page.
2. **For specific tech (NuGet, DB, CLI, build pipeline)** → `01-stack-comparison.md`
   has 1:1 tables organized by topic.
3. **For architectural decisions (actors, LLM gateway, terminal, hardness)** →
   `02-architecture-comparison.md` has diagrams and branching rationale.
4. **For adoption decisions (cost, trade-off, sequencing)** →
   `03-adoption-recommendations.md` has work units with P0~P3 priority.

Only after exhausting these four files should you crawl `D:\Code\AI\AgentWin`
directly. Direct crawls are **slow, context-hungry, and risk drift** — the
snapshot exists precisely to avoid that.

## Snapshot freshness policy

The `Docs/agent-origin/*.md` set is a **time-stamped snapshot** (header in each
file marks the survey date). Refresh trigger:

- More than 6 months since last survey, OR
- User asks about a topic the snapshot does not cover, OR
- A direct crawl reveals divergence from the snapshot

When refreshing:

1. Re-run the per-project survey via parallel `Explore` agents (one for Origin,
   one for Lite) — see the workflow used in
   `harness/logs/tamer/2026-04-27-14-04-agent-origin-comparison-doc.md`.
2. Update the affected `Docs/agent-origin/*.md` file in place (do not create
   new versions — keep one canonical snapshot).
3. Bump the date marker at the top of `README.md`.
4. Log the refresh under `harness/logs/tamer/`.

## Owner agents

- **`tamer`** — answers "compare with origin" / "오리진 참고" requests, refreshes
  the snapshot, links findings into harness improvement proposals.
- **`code-coach`** — when reviewing code in Mode 3 (Research consult), checks
  whether the same problem was solved differently in Origin via the snapshot.
  Cites the relevant `Docs/agent-origin/*.md` section in the proposal.

## Anti-patterns

- ❌ Re-surveying Origin from scratch when the snapshot already covers the
  topic. Wastes context and slows the user.
- ❌ Suggesting wholesale Origin imports without checking
  `03-adoption-recommendations.md`'s **REJECT** section (LLamaSharp removal,
  31-migration accumulation, NoteWindow/HarnessMonitor bloat).
- ❌ Treating snapshot dates as forever-valid. Always check the timestamp first.

## Related project memory

- `memory/reference_agent_origin_docs.md` — pinned across sessions, mirrors
  this knowledge for the user-level memory system.
