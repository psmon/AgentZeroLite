# Harness Creator Rules — binding contract

> **Status**: binding. Tamer (정원지기) **must** read this file before any
> harness modification — adding/editing agents, knowledge, or engines.
> Other actors (specialist agents, skills) reference it when proposing
> harness changes.
>
> **Why a rule file at all**: this harness has decayed twice from drift —
> once when triggers leaked into knowledge files, once when one agent
> tried to invoke another inline. Both episodes rebuilt cleanly by going
> back to the layer separation. This file pins those separations so the
> next decay never happens silently.

---

## The four layers (and what each owns)

| Layer | Directory | Owns | Does NOT own |
|---|---|---|---|
| **Knowledge** | `harness/knowledge/` | *What is correct?* Domain expertise, contracts, anti-patterns, comparative surveys | Triggers · invocation logic · agent dispatch |
| **Agent** | `harness/agents/` | *Who acts?* Personas, **trigger phrases (only here)**, single-axis responsibility, per-agent procedure | Calling another agent inline · multi-agent sequencing |
| **Engine** | `harness/engine/` | *How do agents flow together?* Orchestration of 2+ agents, gating decisions, evaluation rubrics, mermaid sequence | Domain content · trigger phrases (engines have triggers but they fire the *workflow*, not a single agent) |
| **Logs** | `harness/logs/` | *What happened?* Per-agent execution records + per-mission completion logs | Authoring rules · trigger definitions |

---

## Rule 1 — Single-responsibility for agents

Each agent in `harness/agents/<name>.md` covers exactly one expertise axis:

- `tamer` — meta (garden), mission dispatch
- `code-coach` — review / refactor / research consult
- `security-guard` — OWASP + injection + crash forensics
- `build-doctor` — build pipeline + native DLL + version
- `test-runner` — `dotnet test` execution (explicit triggers only)
- `test-sentinel` — structural test landscape audit (no execution)

When you feel an agent file growing into another agent's lane, that's
the signal to either (a) push the cross-lane content into a knowledge
file, or (b) declare an engine that orchestrates both — see Rule 2.

**Anti-pattern (forbidden)**: agent A's procedure says "and then call
agent B for X". If two agents must collaborate, that collaboration lives
in an engine, not embedded in either agent's procedure.

---

## Rule 2 — Multi-agent collaboration → engine

The instant a workflow needs **two or more agents** to coordinate, a
`harness/engine/<name>.md` engine MUST define the orchestration. The
engine owns:

- The sequence (mermaid `flowchart` showing agent A → gate → agent B → ...)
- The gating logic (e.g., release-build-pipeline blocks build-doctor on
  any High/Critical security-guard finding)
- The evaluation rubric (per-engine axes that grade the orchestration
  quality, separate from each agent's own rubric)

Currently shipped engines:

| Engine | Sequence | When |
|---|---|---|
| `release-build-pipeline.md` | security-guard → build-doctor | Pre-release gate |
| `pre-commit-review.md` | code-coach (Mode 2) | Auto-fires before `git commit` if staged dev code |
| `mission-dispatch.md` | tamer → specialist(s) → completion log | "M{NNNN} 수행해" trigger |
| `harness-view-publish.md` | (mostly single-actor) doc-v* tag → Pages CI | Operator runs publish |

If you find yourself writing in an agent file *"once X is done, hand to
Y"* — stop, draft a new engine instead.

---

## Rule 3 — Trigger phrases live in agent frontmatter, nowhere else

`harness/knowledge/**.md` documents *what is correct*. It must not
contain:

- "Trigger on:" sections
- "Trigger | Action" tables
- "When user says X, do Y" instructions
- Frontmatter `triggers:` arrays

All trigger phrases — including the ones that activate engine workflows
— are owned by the agent or engine frontmatter that the operator
actually invokes. If a knowledge document needs to mention an
activation, it points back at the owning agent file:

> *"This protocol activates when tamer dispatches a mission. The trigger
> phrases live with the agent (`harness/agents/tamer.md` frontmatter);
> knowledge files document the contract, not the invocation."*

— from `harness/knowledge/tamer/missions-protocol.md`

---

## Rule 4 — Knowledge owned per-agent (since 2026-05-04)

`harness/knowledge/` is organised into per-agent subdirectories, each
"owned" by the agent that uses the knowledge most:

```
_shared/         — cross-cutting (architecture map every agent anchors against)
tamer/           — missions-protocol, agent-origin-reference
code-coach/      — wpf pitfalls, avalondock, llm-prompt-conventions, …
security-guard/  — security-surface, crash-dump-forensics
test-runner/     — dotnet-test-execution, unit-test-policy
test-sentinel/   — voice-roundtrip-testing
```

When adding a new knowledge file: pick the primary owner. Cross-references
for secondary readers stay as inline links inside the file body.

---

## Rule 5 — Tamer's modification protocol

When tamer enters update / improve / suggest mode (`하네스를 업데이트해`,
`하네스를 개선해`, etc.), the **first action** of the procedure is to
**Read this file (`harness/creator-rule.md`)** so the rules are in
working memory. Only then propose changes. Specifically:

1. **Before adding an agent**: confirm its responsibility doesn't overlap
   any existing agent. If it does, either fold the new responsibility
   into the existing agent or declare an engine that coordinates both.
2. **Before adding an engine**: confirm 2+ agents are involved, and
   no existing engine already covers the sequence.
3. **Before adding knowledge**: pick the per-agent subdirectory that
   owns the topic. Strip any trigger phrases from the draft.
4. **Before moving knowledge**: update the move-checklist in
   `harness/knowledge/README.md` (agent backlinks, engine
   cross-references, `harness/first-world.md` welcome pointer, memory
   pins).

Violations of any rule above = block the change and surface the
violation in the tamer Mode B report.

---

## Rule 6 — Logs follow agents, not workflows

When an engine runs, **each participating agent writes its own log**
under `harness/logs/<agent>/`. The engine itself writes a small
summary log under `harness/logs/<engine>/` that **links** to the
participating agent logs (see
`harness/logs/release-build-pipeline/2026-05-03-20-46-M0009-patch.md`
for the canonical shape).

Engine logs are aggregators, not duplicators. Don't re-state agent
findings inside the engine log; cite + link.

---

## Why these rules together

The four layers are tempting to collapse — *"why not just put the
trigger inside the knowledge file"*, *"why not let code-coach call
test-runner directly"*. Every collapse breaks audit, search, and
the harness viewer's per-layer rendering. Worse, they reintroduce
the hidden coupling that made earlier harnesses ungovernable.

Tamer's job description (정원지기 카카시) is exactly to enforce these
layers as the garden grows. Read this file every time you tend the
garden.
