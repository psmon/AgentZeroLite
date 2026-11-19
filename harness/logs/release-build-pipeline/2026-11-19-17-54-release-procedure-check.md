---
date: 2026-11-19T17:54:17+09:00
agent: engine:release-build-pipeline
type: explanation
mode: log-eval
trigger: "빌드배포 절차확인"
---

# Build & release procedure — check (no release run)

## 실행 요약

Read-only walk of the release path as it is documented and as it actually runs, at `main` @ 234edc8
(1 commit ahead of origin/main; last release tag v0.27.1, version.txt 0.27.1).
Sources: `harness/engine/release-build-pipeline.md`, `harness/agents/build-doctor.md`,
`.claude/skills/agent-zero-build/SKILL.md`, and today's analysis
`harness/logs/build-doctor/2026-11-19-15-41-build-pipeline-analysis.md` (same HEAD — reused, not re-derived).
No build, test, commit, tag or push was performed.

## 결과

### Procedure as documented (engine + skill combined)

| # | Step | Owner | Status in practice |
|---|------|-------|-------------------|
| 0a | security-guard full pass, Critical/High = hard stop | engine Step 1 | **not run since 2026-09-18** (no log for any release after v0.3.2) |
| 0b | audio-regression-review, only if audio paths changed since last `v*` | engine Step 2 (advisory) | not run |
| 0c | build-doctor: Debug build + pipeline checks | engine Step 3 | last log 2026-05-03; definition still checks removed ConPTY pins and builds WPF, not the shipped Avalonia host |
| 1 | Sync main (commit dirty tree, push) | agent-zero-build | used |
| 2 | Bump `Project/version.txt` + commit "chore: bump version to X.Y.Z" | agent-zero-build | used (e.g. 12761c7 → 0.27.1) |
| 2.5 | Optional `ci-<X.Y.Z>` tag → `avalonia-build.yml` (only CI that runs unit tests) | agent-zero-build | optional |
| 3 | Annotated tag `vX.Y.Z` + push → fires `release.yml` | agent-zero-build | used |
| 4 | CI: win-x64 Setup.exe + zip (Latest), macOS .pkg (if secrets), Linux .deb/.tar.gz | release.yml | used |
| 5 | Engine summary log | engine Step 4 | not written since 2026-05-03 |

### Pending if a release were cut now

- Commits since v0.27.1: 4 (2 agent-one features, 2 store docs). agent-one has its own release
  (`agent-one-v*`), so the GUI-relevant change since v0.27.1 is effectively none committed.
- Uncommitted: Agent Stage garden-experts work (`KakashiHarness.cs`, `ClaudeHarnessDuty.cs`, plugin, READMEs) —
  this is what a next release would actually carry.
- Audio lane: no audio-watched path in the 4 commits → would be a no-op.

### Gaps (unchanged from the 15:41 analysis)

1. Engine says the skill "must" call it; the skill never mentions the engine or security-guard → gate is paper-only.
2. build-doctor scope is stale (ConPTY DLL pins removed; Avalonia is the shipped host).
3. agent-zero-build text: artifacts listed as Windows only; conpty recovery tip obsolete; old model in commit example.
4. No unit tests on the release path unless `ci-*` is pushed.

## 평가

| Axis | Result |
|---|---|
| 워크플로우 개선도 | C — the skill path works and ships; the engine around it is not in the loop |
| Claude 스킬 활용도 | 2/5 — agent-zero-build is used, the harness engine it should call is not |
| 하네스 성숙도 | L3 — engine/agents/knowledge exist, but drift between definition and practice |

## 다음 단계 제안

- Choose: wire the gate into `agent-zero-build` (Step 0: security-guard → build-doctor) **or** downgrade the
  engine's "must" to advisory. Either removes the false claim.
- Refresh build-doctor around Avalonia (`dotnet build` Avalonia Debug + `-cli selftest all`, natives under
  `ZeroCommon/runtimes/*/native`, wearable copy/publish targets).
- Refresh agent-zero-build SKILL.md (multi-OS artifacts, drop conpty tip).
