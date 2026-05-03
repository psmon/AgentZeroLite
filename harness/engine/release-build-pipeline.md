---
name: release-build-pipeline
agents: [security-guard, build-doctor]
triggers:
  - "release build"
  - "릴리즈 빌드"
  - "ship a release"
  - "에이전트빌더 배포해"
  - "AgentZero 배포"
auto_invoke_on:
  - skill: agent-zero-build
    note: "agent-zero-build skill must run this engine before tagging."
description: Release-build orchestration. Security-guard runs first as a hard gate; build-doctor only starts if no Critical/High findings remain.
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

2. **Build (build-doctor)** — only if Step 1 produced no Critical/High.
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

3. **Engine log** — write a summary to
   `harness/logs/release-build-pipeline/{yyyy-MM-dd-HH-mm-title}.md` linking both
   step logs and recording the gate decision (passed / waived / blocked).

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

Waiving a Critical/High security finding requires:

- A written justification in the same `security-guard` log file under a `## Waiver`
  section (who, why, expiry date).
- Re-running this engine, which will detect the waiver and proceed — once.
- The waiver does NOT carry over to the next release; each release re-evaluates.

## Coordination with agent-zero-build skill

The `agent-zero-build` skill (which runs the SemVer bump + tag + GitHub Actions handoff)
must call this engine before tagging. If invoked without going through this engine, the
skill should refuse or warn loudly. The pinning lives in
`memory/project_release_security_gate.md`.
