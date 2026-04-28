---
date: 2026-04-25T15:00:00+09:00
agent: tamer
type: improvement
mode: log-eval
trigger: "bash.exe.stackdump 분석 전문 지식과 담당자 내부 지정"
---

# Designate Crash Dump Forensics Owner

## Execution Summary

User flagged a `bash.exe.stackdump` artifact left in the repo root from a Gemma 4
self-build attempt (already resolved). Two requests: (1) designate the right
specialist internally to own this kind of analysis, (2) preserve the knowledge
so the next occurrence is triaged quickly.

User suggested security-side ownership with rationale to be evaluated.

## Owner decision

**Primary**: `security-guard` (per user lean). Rationale held up:
- Crash dumps are memory-forensic artifacts — credential / path leak surface.
- `.stackdump` and especially `.dmp` files can be exploitation signals (recurring
  crashes from similar parent processes, unexpected DLL injection in module list).
- Forensic skill set (redaction, severity calibration, OWASP/CWE mapping) is
  already what security-guard does.

**Secondary**: `build-doctor` for build-environment correlation. The MSYS2 fork
crash signature in this specific case ties back to build-time stress (heavy
parallel native build), which is build-doctor's domain. But the diagnosis itself
(reading the trace, classifying, redacting) stays with security-guard.

## Result — files created/modified

Created:
- `harness/knowledge/crash-dump-forensics.md` — generic playbook
- `harness/knowledge/cases/2026-04-22-msys2-bash-crash-llamacpp-build.md` —
  case study with redacted evidence
- `harness/docs/v1.1.1.md` — patch changelog

Modified:
- `harness/agents/security-guard.md` — +4 triggers, +scope #7, rubric updated
- `harness/agents/build-doctor.md` — +scope #7 for build correlation
- `harness/harness.config.json` — version 1.1.0 → 1.1.1

The actual `bash.exe.stackdump` file:
- Verified `*.stackdump` is in `.gitignore` (regression baseline).
- File stays in working tree (user can delete at will).
- Evidence preserved in the case study doc with redaction applied.

## Diagnosis preview (handed to security-guard for the case file)

Every frame in `msys-2.0.dll` → textbook MSYS2 fork-emulation crash. No
application code, no llama.cpp code, no third-party DLL injection. Build-env
instability under heavy native build load. Standard mitigations enumerated;
user-confirmed resolved but specific applied mitigation pending fill-in
(likely candidate: PowerShell + native Ninja, escaping MSYS2 entirely — which
also aligns with the project's MS-managed-stack posture documented in README).

## Evaluation (3-axis)

### Axis 1 — Workflow Improvement: **B**

No new workflow added. Scope extension on existing agent + new knowledge layer.
Real but bounded improvement: future stackdumps now have a known owner and
playbook instead of "ask Claude what to do". The L3→L4 maturity indicator
(knowledge growing from real observed events) was nudged forward by landing the
first case study.

### Axis 2 — Skill Utilization: **4**

Reused the existing `security-guard` and `build-doctor` agents instead of
creating a new "crash-analyst" agent — correct call, this is not common enough
to justify a dedicated specialist and the work overlaps cleanly with what the
two existing agents already cover. No skill duplication, no new dependencies.

### Axis 3 — Harness Maturity: **L3 → L3+** (still growing)

Knowledge layer doubled (2 → 4 docs) and the first `cases/` subdirectory is
seeded. Logging continues to land. Still need accumulated multi-agent runs
before L4 (안정) is reached.

## Next Steps

1. User fills in the actual applied mitigation in
   `cases/2026-04-22-msys2-bash-crash-llamacpp-build.md` `## Resolution` section
   when convenient.
2. If the same fork-emulation signature reappears (next on-device model build per
   `project_gemma4_self_build_lifecycle.md`), security-guard goes straight to
   the case study — diagnosis is already done.
3. Watch for whether the `cases/` directory accumulates — if it does, the
   playbook itself may need a "common patterns" appendix in a future patch.
