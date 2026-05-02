# missions/ — operator's request inbox

This directory holds **mission files** — written briefs the operator (a human
collaborator) hands to the harness for execution. The protocol lives in
`harness/knowledge/missions-protocol.md`; this README is the quick reference.

## Filename format

```
M{NNNN}-{kebab-case-slug}.md
```

- `M0001`, `M0042`, `M1234` — zero-padded to 4 digits.
- Slug is short, English, kebab-case (e.g. `add-missions-system`,
  `fix-voice-gpu-fallback`, `document-actor-topology`).
- Number is monotonically increasing within this harness; never reused.
- Korean operators should write the *content* in Korean if they prefer; the
  filename slug stays English so it's safe to grep / link from any tooling.

## Mission file contract

Frontmatter (YAML):

```yaml
---
id: M0001                          # required, matches filename
title: 짧은 제목                    # required, free-form (operator's language)
operator: psmon                    # required
language: ko                       # required — ko | en | … (drives output language)
status: inbox | in_progress | done | cancelled   # required, lifecycle marker
priority: low | medium | high       # optional
created: 2026-05-02                # required
---
```

Body sections (free-form, all optional but recommended):

- `# 요청 (Brief)` — the actual ask, vibe-level OK.
- `## Acceptance` — checklist the operator considers "done".
- `## Notes` — links, prior context, constraints.

The file IS the request log. It does not need to be copied elsewhere.

## How a mission gets executed

1. Operator says **"M{NNNN} 수행해"** / **"M{NNNN} 진행해"** /
   **"run mission M{NNNN}"**.
2. The tamer (kakashi summon, via `/harness-kakashi-creator`) reads
   `harness/missions/M{NNNN}-*.md`.
3. Tamer classifies the mission and dispatches to the matching specialist(s):
   - code change → `code-coach` (or direct edit)
   - build / release → `build-doctor` / `release-build-pipeline`
   - test execution → `test-runner` (only if mission explicitly requests it)
   - security review → `security-guard`
   - new agent / harness update → tamer itself (Mode B)
   - documentation only → tamer or specialist depending on topic
4. After execution, the **completion log** is written to
   `harness/logs/미션기록/M{NNNN}-수행결과.md` in the **operator's language**
   (per the mission's `language` field).
5. The mission file's `status` is updated `inbox → in_progress → done`.

## Language policy (mission-scoped override)

Inside the missions subsystem, output language **matches the requester**.
This overrides the harness-wide "English-only artifacts" convention for
mission files and `harness/logs/미션기록/` entries only. Other harness
artifacts (agents, knowledge, engine) remain English.

## Quick example

`harness/missions/M0042-document-actor-topology.md`:

```markdown
---
id: M0042
title: Actor 계층 다이어그램 추가
operator: psmon
language: ko
status: inbox
priority: medium
created: 2026-05-10
---

# 요청
CLAUDE.md의 Actor topology 섹션이 텍스트뿐이다. mermaid 다이어그램을
추가해서 신규 컨트리뷰터가 한눈에 파악하게 해줘.

## Acceptance
- [ ] StageActor → AgentBotActor / WorkspaceActor / TerminalActor 트리
- [ ] ITerminalSession 시암 표시
- [ ] CLAUDE.md에 인라인 삽입
```

Operator: **"M0042 수행해"**

→ tamer reads M0042, dispatches to documentation work, edits CLAUDE.md,
writes `harness/logs/미션기록/M0042-수행결과.md` in Korean.
