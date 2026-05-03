# Missions protocol — file-based request management

> Status: binding. Pinned in `memory/project_missions_protocol.md`.
> Owner: tamer agent. Quick reference: `harness/missions/README.md`.
> Workflow diagram + evaluation rubric: `harness/engine/mission-dispatch.md`.

## Why this exists

Operator-to-harness requests used to live entirely in chat — vibe-level,
ephemeral, hard to audit later. The missions subsystem captures requests as
files so:

- The operator can write a brief, walk away, and come back with
  **"M{NNNN} 수행해"** to fire it. No re-explaining context.
- Multiple in-flight requests can coexist with explicit numbering.
- The completion log per mission becomes a portfolio of "what was asked,
  what was done, what was learned" — useful for retrospection without
  trawling chat history.

This is "vibe mode but file-based" — the request itself can be informal,
the lifecycle is formal.

## File layout

```
harness/
├── missions/
│   ├── README.md                              ← quick reference
│   ├── M0001-add-missions-system.md           ← mission file
│   ├── M0042-document-actor-topology.md
│   └── …
└── logs/
    └── mission-records/                                ← completion logs (operator-language)
        ├── M0001-수행결과.md
        ├── M0042-수행결과.md
        └── …
```

The **directory name is English** for tooling safety (grep, paths in URLs,
git, IDE outline panes). The **files inside** can be named in the operator's
language — see "Completion log filenames" below — and the **body content** is
always in the operator's language per the "Language policy" section.

Earlier drafts (v1.3.0) used a Korean directory name (`미션기록/`); v1.3.1
renamed it to `mission-records/` after operator feedback that mixed-script
paths complicate external tooling.

## Mission file contract

```yaml
---
id: M0001                          # required, matches filename
title: 짧은 제목                    # required, operator's language
operator: psmon                    # required
language: ko                       # required — drives completion-log language
status: inbox | in_progress | done | cancelled
priority: low | medium | high       # optional
created: 2026-05-02                # required, ISO date
related: [M0000, …]                 # optional, prior missions this builds on
---

# 요청 (Brief)
{free-form ask, vibe-level OK}

## Acceptance
- [ ] {checkbox}

## Notes
{constraints, links, prior context}
```

### Rules

- `id` is monotonically increasing 4-digit number (`M0001`, `M0042`,
  `M9999`). Never reused; never re-numbered after creation.
- Filename = `{id}-{kebab-slug}.md`. Slug is English so external tooling
  (grep, links, GitHub UI) is safe.
- `language` field is the source of truth for output language. Default to
  the language the *brief body* is written in.

## Activation

This protocol activates when tamer dispatches a mission. The **trigger
phrases** themselves live with the agent (`harness/agents/tamer.md`
frontmatter); knowledge files document the contract, not the
invocation. The orchestration sequence is captured in
`harness/engine/mission-dispatch.md`.

## Dispatch — how tamer picks the specialist

Tamer reads the mission body and chooses based on *what's being asked*:

| Mission flavor | Dispatch target |
|---|---|
| Code change / refactor / fix (cross-stack) | `code-coach` (review-then-edit) or direct edit when scope is tiny |
| Build pipeline / version.txt / native DLL pinning | `build-doctor` |
| Release / deploy / ship | `release-build-pipeline` engine (security-guard → build-doctor → handoff) |
| Test execution (only if mission explicitly asks) | `test-runner` |
| Test landscape / coverage audit | `test-sentinel` |
| Security review / prompt-injection / IPC surface | `security-guard` |
| New agent / new knowledge / new engine / harness update | tamer itself (Mode B suggestion-tip) |
| Documentation / explanation / walkthrough | tamer or topic-relevant specialist |
| Multiple of the above | Tamer orchestrates as a sub-engine, calling each in order |

Ambiguous? Tamer asks the operator one clarifying question, then proceeds.

## Language policy (mission-scoped)

The harness-wide convention is "all artifacts in English"
(`memory/feedback_harness_language_policy.md`). The missions subsystem
**overrides** that convention as follows:

| Artifact | Language |
|---|---|
| Mission file body (`harness/missions/*.md`) | Operator's choice (whatever the brief is written in) |
| Mission filename slug | **English** (kebab-case) — for tooling safety |
| Mission `missions/` and `logs/mission-records/` directory names | **English** — for tooling safety |
| Completion log file body | **Match the mission's `language` field** |
| Completion log filename slug | May match the mission's language (e.g. `M0001-수행결과.md` for ko, `M0002-execution-result.md` for en) |
| Tamer / specialist agent files (`harness/agents/*.md`) | English (unchanged) |
| Knowledge / engine / docs (`harness/knowledge/*`, `harness/engine/*`, `harness/docs/*`) | English (unchanged) |

Rationale: the operator is the audience for completion logs. Matching their
language reduces friction. Internal harness artifacts keep English so
specialist agents and external tooling don't fragment.

## Pencil design artifacts (optional)

If a mission's brief asks for a Pencil (`.pen`) design step (operator
phrases like "펜슬로 디자인 작업을 먼저 검토" or "draw the layout in
Pencil first"), the design file MUST land at:

```
Docs/design/M{NNNN}-{english-kebab-slug}.pen
```

Filename rules mirror Rule 1: the `M{NNNN}` prefix at the start lets
the harness-view indexer pair the design with the mission. The slug
after the ID is English kebab-case for tooling safety (Pencil paths
end up in URLs / PowerShell args).

The viewer pairs the design automatically — no view change needed.
Missions card adds an `✎ design` chip on the sticky note and a
**Design (.pen)** tab inside the modal that renders the frames inline
via `pen-renderer.js`. The full contract for indexer pairing lives at:

> **`.claude/skills/harness-view-build/references/data-contracts.md`**
> "Rule 5 — Mission designs must start with `M{NNNN}`"

| Anti-pattern | Why it fails |
|---|---|
| `Docs/design/webdev-mainmenu.pen` | No `M{NNNN}` prefix — indexer can't pair it |
| `Docs/design/M0006-webdev-확장.pen` | Korean slug works for filesystem but breaks PowerShell / URL escaping in some tools — prefer English |
| `Docs/design/2026-05-03-M0006-...pen` | Timestamp prefix — `^M\d+` regex fails |

Multiple `.pen` per mission is allowed (Rule 5 picks the first match
deterministically); when in doubt, ship one canonical file per
mission.

## Completion log contract

Path: `harness/logs/mission-records/M{NNNN}-{slug}.md`

> **Filename is enforced by the harness-view indexer**, not just convention.
> `Home/harness-view/scripts/build-indexes.js:431` matches mission records
> with regex `^(M\d+)\b` — anything that doesn't start with the mission ID
> is silently dropped, leaving the Missions card's `recordFile` null even
> when work was completed. The full contract (regex source, anti-patterns,
> frontmatter shape consumed by the indexer) lives at
> **`.claude/skills/harness-view-build/references/data-contracts.md`** —
> consult that file before changing this section.

Slug rules:
- Korean missions → `M{NNNN}-수행결과.md` (Korean slug OK).
- English missions → `M{NNNN}-execution-result.md` (or any English kebab-case).
- Other languages → analogous; the file lives next to peers and grep tolerates
  Unicode filenames, but English is always a safe default if uncertain.
- **Never** prefix with timestamp / `mission-` / any other token that pushes
  `M{NNNN}` off the start of the filename.

Frontmatter (mirror of the mission's metadata + execution outcome):

```yaml
---
mission: M0001
title: {복사 from mission}
operator: {복사 from mission}
language: {복사 from mission}
dispatched_to: [tamer, code-coach, …]   # which specialists ran
status: done | partial | blocked | cancelled
started: 2026-05-02T08:43:00+09:00
finished: 2026-05-02T09:15:00+09:00
artifacts:                                # files touched (concise list)
  - harness/missions/M0001-…md
  - harness/agents/tamer.md
  - …
---
```

Body sections (in operator's language):

- `# 수행 요약` / `# Execution summary`
- `## 결과 / Result`
- `## 변경 파일 / Files changed`
- `## 평가 / Evaluation` (against acceptance checklist)
- `## 비고 / Notes` (anything the next reader will care about)
- `## 다음 미션 제안 / Next-mission suggestions` (optional)

The completion log is **independent** from individual specialist logs
(`harness/logs/{agent}/*`) — those continue to exist for per-specialist
audit. The mission completion log is the *operator-facing* aggregate.

## Lifecycle

```
status: inbox        ← operator just wrote the file
status: in_progress  ← tamer started executing it
status: done         ← completion log written, acceptance items addressed
status: cancelled    ← cancelled at any time (with rationale in completion log)
```

Tamer flips `status` in-place via Edit, never deletes the mission file.
The file IS the audit trail.

## What this protocol does NOT do

- Does not block missions on each other automatically. Operator orders.
- Does not auto-run tests, even if the mission touched code (per
  `harness/knowledge/test-runner/unit-test-policy.md`). The mission must explicitly
  ask for tests via the test-runner triggers.
- Does not create CI / hooks / cron — mission execution is on demand,
  triggered by the operator's spoken phrase.
- Does not number missions automatically. Operator picks the next free
  `M{NNNN}` (tamer can suggest if asked, e.g. "다음 미션 번호").

## Coordination with existing harness

| Existing | Interaction with missions |
|---|---|
| tamer | Owns mission dispatch + completion log writing |
| pre-commit-review engine | Still fires when a mission's edits hit `git commit` (no change) |
| release-build-pipeline | Can be the dispatch target of a "릴리즈" mission |
| test-runner | Dispatch target only when mission explicitly says so |
| harness-wide language policy | Overridden inside the missions subsystem only |
| `harness/docs/v{x.y.z}.md` | Mission completion logs are NOT version docs — keep separate |
