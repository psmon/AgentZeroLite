# First World — How This Harness Was Born

> Genesis story of the **AgentZero Lite Harness** (kakashi-style). Written right after
> v1.1.0 was planted so a future contributor — or future-Claude — can pick up exactly
> where this started and why each piece is where it is.
>
> Korean conversational transcript: [`../first-world.md`](../first-world.md)
> Skill used: [`harness-kakashi-creator`](https://github.com/psmon/harness-kakashi)

---

## 0. Before the Garden

The project already had:
- A working .NET 10 WPF app (`AgentZero Lite`) brokering multiple ConPTY terminals.
- A bilingual `README.md` / `README-KR.md` with — at the very top — a Security Notice
  flagging that the AgentChatBot's keystroke forwarding turns prompt injection into
  a real OS-command-execution surface.
- A `Docs/llm/` subtree documenting the *temporary* self-built `llama.dll` workaround
  for Gemma 4, with explicit warnings that the prebuilt DLLs are not vouched for.
- A `agent-zero-build` skill that handles version bump → tag → GitHub Actions release.
- Memories (`MEMORY.md` + 2 entries) covering the Gemma 4 lifecycle and harness
  language policy.

What it did **not** have:
- Any structured way to enforce that the README's Security Notice promise is actually
  honoured before a release ships.
- Any structured way to keep code review in the loop instead of one-off requests.
- Any single place where the project's hard-won facts (Akka shutdown deadlock history,
  ConPTY IDE-attachment trap, native DLL pinning) lived as machine-readable knowledge
  for agents to anchor on.

The harness exists to fill exactly those gaps.

---

## 1. The Naming — `init`

```
/harness-kakashi-creator init
```

The init step required two pieces of identity:

| Field | Value |
|-------|-------|
| `name` | `AgentZero Lite Harness` |
| `description` | *"QA & research garden for AgentZero Lite — Windows-native multi-CLI shell with on-device LLM experiments and prompt-injection-aware security review"* |

The description was deliberate. By naming **prompt-injection-aware security review** in
the harness identity itself, every later agent decision could anchor on that — not as
a "nice to have" but as the garden's stated purpose.

A language policy was also pinned: **harness artifacts are written in English; user
conversation stays in Korean.** This makes the garden portable and AI-friendly across
sessions while keeping the live conversation natural for the maintainer. (Saved in
[`memory/feedback_harness_language_policy.md`](../../../memory/feedback_harness_language_policy.md).)

The result was a v1.0.0 garden with one resident — the gardener
([`tamer.md`](../../../harness/agents/tamer.md)) — and three empty soil layers
(`knowledge/`, `agents/`, `engine/`).

---

## 2. The First Three Flowers

The gardener proposed three specialists tailored to *this* project's nature, not a
generic starter pack:

### [security-guard](../../../harness/agents/security-guard.md)

The README had already named this risk in writing. The agent operationalises it:
six scoped scan items (injection surface, actor name sanitization, IPC, native binary
trust, persistence, dependency drift), severity calibration against OWASP/CWE, and
file:line + concrete-fix outputs. It is also the **gate** for release builds — see §4.

### [build-doctor](../../../harness/agents/build-doctor.md)

This project ships an installer whose silent failure modes are nasty: a NuGet
package version bump without updating the hard-coded `<Content Include=...>` paths
in `AgentZeroWpf.csproj` produces an installer that builds, packages, and *runs* —
but the ConPTY tabs never start because the native DLLs are missing. The Doctor's job
is to prevent that and to keep the version pipeline (`version.txt` auto-bump → tag →
Inno Setup → GitHub Actions) consistent.

### [test-sentinel](../../../harness/agents/test-sentinel.md)

The project has two test projects with a strict boundary: `ZeroCommon.Tests` is
headless (must stay WPF-free), `AgentTest` needs a desktop session (Akka actors,
ConPTY, ApprovalParser). The Sentinel watches that boundary and the project's
historical pain points — the Akka shutdown deadlock that left the single-instance
mutex held, the ConPTY garbled-output trap when an IDE attaches its console, etc.

---

## 3. The Fourth Flower — code-coach

The user's request for the fourth agent was specific and shaped the design directly:

> *"There's no expert helping write good code right now. Add one — Code Coach. It
> reviews what was just written (manual, in-session, before commit), but also fires
> automatically right before `git commit` if the staged diff has dev code. This
> coach is a .NET-modern / Akka.NET / WPF / LLM / Win32-native expert and also a
> good tech writer — when I say 'this is hard to solve', they research it and
> propose options."*

That is three modes in one agent, deliberately:

| Mode | Trigger | Output |
|------|---------|--------|
| **Manual review** | User says "방금 작성한 거 리뷰해줘" / "code review" | Per-file findings on uncommitted diff |
| **Auto pre-commit** | Claude is about to `git commit` AND staged diff has `*.cs` / `*.xaml` / `*.csproj` etc. | Inline findings before commit; advisory, not blocking |
| **Research consult** | User says "X 해결하기 어려워" / "research X" | Option A/B/C with tradeoffs + recommended option + concrete next step; optional knowledge note in `harness/knowledge/` |

Definition: [`code-coach.md`](../../../harness/agents/code-coach.md).
The auto-trigger is pinned in
[`memory/project_pre_commit_code_coach.md`](../../../memory/project_pre_commit_code_coach.md)
so the convention survives session boundaries.

---

## 4. Two Watercourses (Engines)

Engines are how flowers cooperate. Two were planted:

### [release-build-pipeline](../../../harness/engine/release-build-pipeline.md)

```
security-guard (full pass on HEAD)
       │
   ┌───┴───┐
   │ Gate? │   any Critical/High (no waiver) → STOP
   └───┬───┘
       │ pass
       ▼
build-doctor (version pipeline, native DLL pinning, build configs)
       │
       ▼
hand off to agent-zero-build skill (tag → push → GitHub Actions)
```

This mechanizes the README Security Notice. Before this engine, the promise was
honourable but unverified. After this engine, a release that hasn't passed
`security-guard` cannot tag.

Cross-session pin:
[`memory/project_release_security_gate.md`](../../../memory/project_release_security_gate.md).

### [pre-commit-review](../../../harness/engine/pre-commit-review.md)

```
git commit (about to run)
       │
       ▼
git diff --cached --name-only
       │
   ┌───┴────────────────────┐
   │ doc-only? (.md, Docs/) │── yes → skip, commit directly
   └───┬────────────────────┘
       │ no — has dev code
       ▼
code-coach Mode 2 (review staged diff)
       │
       ▼
findings inline → user proceeds, fixes, or says "ignore and commit"
```

Deliberately **not** a `.claude/settings.json` PreToolUse hook — a hook would force
the review on every session in this repo, including throwaway hotfixes. The
convention version lets Claude skip when context makes clear the user wants speed.

---

## 5. Two Soils (Knowledge)

Two knowledge docs were seeded — the highest-leverage ones for this project:

### [agentzerolite-architecture.md](../../../harness/knowledge/agentzerolite-architecture.md)

Quick-reference map of the load-bearing pieces: two-project dependency rule,
single-exe two-modes, actor topology, the Akka shutdown invariant (with its
deadlock history), CLI ↔ GUI IPC details, EF migration locations, native DLL
pinning rules, the IDE debugging trap. Agents read this before every pass so that
file:line findings stay anchored in the same vocabulary.

### [security-surface.md](../../../harness/knowledge/security-surface.md)

The injection-aware view: every path where untrusted text becomes an OS-visible
action. The primary surface (AgentChatBot CHT/KEY modes), the secondary surface
(`WM_COPYDATA` IPC + memory-mapped files), native binary trust, local data at rest,
single-instance enforcement, workspace/terminal name handling, markdown/Pencil
rendering. `security-guard` reads this before every pass.

---

## 6. Memory Pins (Cross-Session Durability)

Four entries in the user-level memory index work together:

| Memory | Role |
|--------|------|
| [`feedback_harness_language_policy.md`](../../../memory/feedback_harness_language_policy.md) | Harness writes in English; user conversation stays in Korean |
| [`project_release_security_gate.md`](../../../memory/project_release_security_gate.md) | Releases must pass `release-build-pipeline` before tagging |
| [`project_pre_commit_code_coach.md`](../../../memory/project_pre_commit_code_coach.md) | Pre-commit auto-invoke condition for `code-coach` |
| [`project_gemma4_self_build_lifecycle.md`](../../../memory/project_gemma4_self_build_lifecycle.md) | Self-built `llama.dll` is a temporary workaround; `security-guard` cross-references this when scoping native binary trust |

Without these, the conventions would die at session boundaries. With them, a fresh
Claude session in this repo immediately knows the rules.

---

## 7. The Initial Operating Frame

Putting it all together — when does each piece fire?

```
                       ┌────────────────────────────────────┐
 user asks for code →  │ (no auto-fire; user just gets code)│
                       └────────────────────────────────────┘
                                       │
                                       ▼
                       ┌────────────────────────────────────┐
 git commit pending →  │ pre-commit-review engine           │
                       │   ↪ code-coach (if staged dev code)│
                       │   ↪ skip (if doc-only)             │
                       └────────────────────────────────────┘
                                       │
                                       ▼
                       ┌────────────────────────────────────┐
 user says release  →  │ release-build-pipeline engine      │
                       │   1. security-guard (gate)         │
                       │   2. build-doctor                  │
                       │   3. hand off to agent-zero-build  │
                       └────────────────────────────────────┘

 user says            ┌────────────────────────────────────┐
 "review my tests" →  │ test-sentinel (manual, standalone) │
                      └────────────────────────────────────┘

 user says "X is     ┌────────────────────────────────────┐
 hard, research it"→ │ code-coach Mode 3 — research       │
                     │   ↪ option A/B/C, recommendation,  │
                     │     optional knowledge note write  │
                     └────────────────────────────────────┘

 user says           ┌────────────────────────────────────┐
 "explain harness" → │ tamer (gardener) — meta agent      │
                     └────────────────────────────────────┘
```

Two of the five agents (`security-guard`, `code-coach`) are reachable both manually
**and** as engine steps. Two others (`build-doctor`, `test-sentinel`) are also
manually triggerable, but `build-doctor`'s release path is gated. `tamer` is the
meta-layer — it shapes the garden itself.

---

## 8. Component Index

| Layer | File | Role |
|-------|------|------|
| Config | [`harness.config.json`](../../../harness/harness.config.json) | Garden identity (name, description, agents, engines) |
| Agent | [`agents/tamer.md`](../../../harness/agents/tamer.md) | Gardener (meta) |
| Agent | [`agents/security-guard.md`](../../../harness/agents/security-guard.md) | OWASP + injection-aware security review |
| Agent | [`agents/build-doctor.md`](../../../harness/agents/build-doctor.md) | Build pipeline + native DLL pinning |
| Agent | [`agents/test-sentinel.md`](../../../harness/agents/test-sentinel.md) | Headless/WPF boundary + coverage hot spots |
| Agent | [`agents/code-coach.md`](../../../harness/agents/code-coach.md) | Cross-stack senior + tech writer + research consultant |
| Engine | [`engine/release-build-pipeline.md`](../../../harness/engine/release-build-pipeline.md) | security-guard → gate → build-doctor |
| Engine | [`engine/pre-commit-review.md`](../../../harness/engine/pre-commit-review.md) | Auto-invoke code-coach before `git commit` |
| Knowledge | [`knowledge/agentzerolite-architecture.md`](../../../harness/knowledge/agentzerolite-architecture.md) | Project structure, dependency rule, actor topology, historical incidents |
| Knowledge | [`knowledge/security-surface.md`](../../../harness/knowledge/security-surface.md) | Prompt-injection → OS-exec map; what `security-guard` reads first |
| Docs | [`docs/v1.1.0.md`](../../../harness/docs/v1.1.0.md) | Version 1.1.0 changelog |
| Log | [`logs/tamer/2026-04-25-1440-plant-four-specialists.md`](../../../harness/logs/tamer/2026-04-25-1440-plant-four-specialists.md) | Creation log + 3-axis self-evaluation |

Pointer inside the harness: [`harness/first-world.md`](../../../harness/first-world.md).

---

## 9. Maturity Snapshot

Per [`references/evaluation.md`](https://github.com/psmon/harness-kakashi) (the
3-axis rubric the gardener uses):

| Axis | At init (v1.0.0) | After this session (v1.1.0) |
|------|------------------|------------------------------|
| Workflow improvement | (none — empty garden) | **A** — 0 → 2 engines, README security promise mechanized |
| Skill utilization | (none) | **4 / 5** — proper delegation to `agent-zero-build`; convention not hook |
| Harness maturity | **L1** (empty) | **L3** — *growing* (5 agents + 2 workflows + 2 knowledge docs threshold) |

To reach **L4** (*stable*): accumulated logs across multiple agent runs + repeated
feedback-loop activations + at least one knowledge doc generated by `code-coach`
Mode 3 from a real research request.

---

## 10. What's Next

Suggested first activations to get the garden producing real signal:

1. `security-guard` baseline run on current `HEAD` — seed the first non-creation log.
2. `test-sentinel` baseline run — record current coverage and any boundary status.
3. Next `git commit` of dev code — verify `pre-commit-review` auto-fires and produces
   the first `code-coach` log.
4. Next release attempt — verify `release-build-pipeline` gate works end-to-end with
   the `agent-zero-build` skill.

After those four, the garden has real data to evolve from. The gardener can run
`하네스를 개선해` to evaluate against the 3-axis rubric and propose targeted
upgrades — and the cycle starts.

---

## Appendix — Why "Kakashi"?

The harness skill is **kakashi-style** — a Naruto reference. Kakashi sensei doesn't
fight first; he reads each student's strengths and dispatches the right one to the
right mission. He also has the Sharingan: he can copy a technique just by watching it.

Mapped to this harness:
- The gardener (`tamer`) is the dispatcher.
- The specialists (`security-guard`, `build-doctor`, `test-sentinel`, `code-coach`)
  are the students with distinct strengths.
- The engines are the formations they fight in together.
- The "skill copy" power maps to the harness's ability to seed a new agent or
  knowledge doc by observing existing skills (the *Mode C: Kakashi Copy* path,
  not yet exercised in this garden).
