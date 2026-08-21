---
name: release-build-pipeline
agents: [security-guard, build-doctor]
advisory_lanes: [audio-regression-review]   # parallel advisory review, NOT a hard gate
triggers:
  - "release build"
  - "릴리즈 빌드"
  - "ship a release"
  - "에이전트빌더 배포해"
  - "AgentZero 배포"
auto_invoke_on:
  - skill: agent-zero-build
    note: "agent-zero-build skill must run this engine before tagging."
description: Release-build orchestration. Security-guard runs first as a hard gate; audio-regression-review runs as a conditional advisory lane (only when the diff touches an audio-watched path); build-doctor only starts if no Critical/High findings remain.
---

# Release Build Pipeline

## Why this engine exists

A release build of AgentZero Lite ships an installer that grants the user an AI-driven
shell with OS-level reach. The README's Security Notice promises end-users that the code
has been self-reviewed before installation. This engine is how that promise becomes
mechanical instead of vibes.

## Steps

1. **Pre-flight (security-guard)** — full repo security pass against `HEAD`.
   - Touches every scope item in `harness/agents/security-guard.md`.
   - Writes log under `harness/logs/security-guard/`.
   - **Gate**: any **Critical** or **High** finding → engine **stops here**.

2. **Audio regression review (audio-regression-review) — conditional advisory lane** —
   runs *only if the release diff since the last `v*` tag touches an audio-watched path*.
   - **Scope detection** — `git diff <last v* tag>...HEAD --name-only`, matched against
     the "Files this agent watches" lists in `harness/agents/music-curator.md` and
     `harness/agents/voice-curator.md`. No audio path touched → **no-op** (one-line
     engine log, exit clean, cheap).
   - **Route** — music paths → `music-curator`; voice paths → `voice-curator`; both may
     fire (shared `LoopbackCaptureService` ripples into Voice's capture path). They run
     **independently** (Rule 1); the engine collects both logs.
   - **Verdict** — **advisory** by default (findings surfaced, release proceeds). A
     curator **Pass/Fail-axis Fail** (broken capture format, wrong diarization pair,
     non-16 kHz sample rate) escalates to **recommend-block**.
   - **NOT a hard gate** — unlike `security-guard`, this lane informs the operator who
     owns the release decision. Waivers follow the same protocol as Step 1: a `## Waiver`
     section in the failing curator's log (who, why, expiry), and the engine proceeds
     once. Waivers do NOT carry over to the next release.
   - Full contract in `harness/engine/audio-regression-review.md` (this lane is the
     wiring that file's "Not yet" deferral was waiting on — now live).
   - Writes log under `harness/logs/audio-regression-review/` linking the curator logs
     (Rule 6 — aggregator, not duplicator).

3. **Build (build-doctor)** — only if Step 1 produced no Critical/High.
   - Validates version pipeline, native DLL pinning, csproj configurations,
     EF migrations location, and `App.OnStartup` `-cli` detection.
   - Runs `dotnet build -c Debug` to confirm a clean compile.
   - **Does NOT run `dotnet test`** — per `harness/knowledge/test-runner/unit-test-policy.md`
     the release pipeline no longer auto-executes the unit suite. If the user
     wants tests run before tagging, they invoke `test-runner` ("전체 유닛테스트
     수행해") explicitly before initiating the release.
   - Writes log under `harness/logs/build-doctor/`.
   - On success → hand off to the `agent-zero-build` skill (or whatever release path
     the user invoked) for tag + push + GitHub Actions.

4. **Engine log** — write a summary to
   `harness/logs/release-build-pipeline/{yyyy-MM-dd-HH-mm-title}.md` linking the
   security-guard log, the audio-regression-review log (if the lane ran), and the
   build-doctor log, and recording each gate decision (passed / waived / blocked).

## Input

- Trigger phrase from user, OR
- Invocation from `agent-zero-build` skill, OR
- Direct call from `tamer` during a hardening pass.

## Output

- **Pass** → release proceeds; build artifact + tag + GitHub release published.
- **Block** → engine surfaces blockers with file:line + remediation; nothing is tagged
  or pushed. User must fix the blockers (or write an explicit waiver into the log
  with justification) before re-running.

## Waiver protocol

Waiving a **security-guard** Critical/High finding requires:

- A written justification in the same `security-guard` log file under a `## Waiver`
  section (who, why, expiry date).
- Re-running this engine, which will detect the waiver and proceed — once.
- The waiver does NOT carry over to the next release; each release re-evaluates.

Waiving a **recommend-block** from the `audio-regression-review` advisory lane
(Step 2) uses the same protocol, but the `## Waiver` section lives in the
**failing curator's log** (`harness/logs/music-curator/…` or
`harness/logs/voice-curator/…`), not the security-guard log. The engine detects it
and proceeds once; the waiver does not carry over to the next release.

## Coordination with agent-zero-build skill

The `agent-zero-build` skill (which runs the SemVer bump + tag + GitHub Actions handoff)
must call this engine before tagging. If invoked without going through this engine, the
skill should refuse or warn loudly. The pinning lives in
`memory/project_release_security_gate.md`.

## Cross-references

- Engine: `harness/engine/audio-regression-review.md` — the advisory audio lane wired
  in at Step 2. It stays a parallel advisory lane (not a hard gate); this engine's
  hard gate remains `security-guard → build-doctor`.
