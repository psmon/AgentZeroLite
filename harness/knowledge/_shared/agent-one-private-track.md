# agent-one — separate private improvement track (observe only)

> Observation note for the garden. The harness **watches** this track; it does not drive it, and nothing
> from it is committed into this repo.

## What it is

agent-one capability research + private CLI improvement proceeds in a **separate workspace outside this repo**:

- **Location**: `C:\code\zero-test` → relative to this repo root: `../../zero-test`
- **Nature**: owner-authorized security self-testing used to raise agent-one's capability. The operator owns
  the live target used for the exercise. Artifacts, scripts and any findings live **only** in that private
  workspace (`C:\code\zero-test\security-research\`, with `INVENTORY.md` + `PROGRESS.md`; `agent.md` at the
  root is the resume brief agent-one is pointed at).
- **Build under test**: the **Debug** build of agent-one from `Project/AgentOne` (v0.8.6+), invoked via
  `C:\code\zero-test\ao1.ps1`. Bare `agent-one` on PATH is the stale npm-global release (v0.6.1) — not the
  improvement target.
- **Capability note**: agent-one does not auto-read `agent.md`/`AGENTS.md`; its only auto-context is the
  per-workspace `memory.md`. A fresh workspace resumes only when the operator points it at `agent.md`.

## Rules this repo keeps

- **Do not commit security-research artifacts here.** They were relocated out of this repo on 2026-11-03;
  this repo manages only the long-term task scaffolding and the tool (agent-one source), never the private
  target work.
- **Target knowledge is private.** The site, its endpoints, any findings, page/chat content live in the
  private track space (`<track>/knowledge/`, e.g. `C:\code\zero-test\knowledge\`), never in `harness/`. The
  harness keeps only generic, target-free capability/methodology. (This repo is public.)
- The improvement work on `Project/AgentOne` that lands here is ordinary source change (features, debug
  paths, tests) — reviewed by the normal agents (`code-coach`, `security-guard`, `test-sentinel`).
- When agent-one's behaviour on the private track suggests a source change, it arrives here as a normal
  commit to `Project/AgentOne`, decoupled from the target work.

## Why separate

The target work must never leak into a frequently-released public repo, and the capability research needs a
build the operator controls (Debug), not the published release. Keeping the two apart lets the tool improve
in the open while the exercise stays private.
