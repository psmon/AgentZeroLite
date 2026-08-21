---
date: 2026-08-21T11:05:00+09:00
agent: pr-review
type: review
mode: log-eval
engine: pr-review
trigger: "최근 pr및 master보다 이후 작업된 브랜치 확인해..머지승인을 준비해죠"
pr: 12
head: feat/p0-cleanup
base: main
head_sha: c1f2d03
base_sha: 3968fd1
---

# PR #12 (feat/p0-cleanup) — Merge-approval verification

## 실행 요약

operator 요청으로 "master 이후 작업된 브랜치 + 열린 PR"을 조사하고, 유일한
머지 후보인 **PR #12 `feat/p0-cleanup`** 에 대해 `pr-review` 엔진의 증거 체인을
실행했다. 엔진 원칙대로 **머지/승인 API 호출은 하지 않았다** — 결정은 operator 몫.

### 브랜치 지형 (git fetch --all --prune 기준)

| 브랜치 | PR | 상태 | vs origin/main |
|--------|----|----|----------------|
| feat/p0-cleanup | #12 | **OPEN, MERGEABLE** | +3 commits (머지 후보) |
| feat/herdr | #11 | MERGED | ahead 0 |
| feat/orca | #10 | MERGED | ahead 0 |
| feat/avalonia-crossplatform | 없음 | 로컬 실험 | +6 / behind 71 (오래됨) |
| fix/voice-note-loopback-crash | 없음 | 로컬 | ahead 0 (고유 작업 없음) |

→ 실질 머지 대상은 **PR #12 하나뿐.**

## 결과 (증거 체인)

| 단계 | 명령/대상 | 측정값 | 판정 |
|------|-----------|--------|------|
| Pre-flight | `git status --short` | clean | ✅ |
| Build (build-doctor) | `dotnet build AgentZeroWpf -c Debug` | **0 errors / 10 warnings** | ✅ |
| Unit head (test-runner) | `dotnet test ZeroCommon.Tests` | **484 pass / 0 fail / 24 skip (508)** in 35s | ✅ |
| PR body claim 검증 | 본문 "483 pass / 0 fail" | 실측 484 pass / 0 fail — 주장 이상 충족 | ✅ |
| Code review (code-coach) | ExternalAgentLoop, OsControl, ElementTreeScanner diff | 아래 참조 | ✅ |
| Security (security-guard) | trust-boundary on changed paths | 신규 신뢰경계 유입 없음 | ✅ |

### 경고 분류 (엔진 규약: count 아닌 root-cause)

10 warnings = **단일 근본원인**. 전부 `NU1903` — `System.Security.Cryptography.Xml`
10.0.7 패키지의 알려진 취약점 하나가 여러 GHSA advisory × 2 프로젝트로 중복 표시된
것. **이 PR이 건드린 파일 아님(기존 부채)** → 아래 out-of-scope로 분리.

### 코드 리뷰 요지 (in-scope)

- **P0-2 자가교정 (ExternalAgentLoop.cs)** — `_formatCorrections` per-instance 카운터,
  `MaxFormatCorrections`(default 2) cap. "envelope 부재" 경로에만 교정 주입,
  unparseable JSON은 기존 `TryRepairAsDoneEnvelope` 계약 유지 → 스코프 명확, budget
  leak 없음. 회귀 테스트가 성공/ cap 초과 fail-fast / per-instance cap 3경로 커버.
- **#13 UIA hang 방지 (OsControlService/ElementTreeScanner)** — STA thread를
  `Join(timeoutMs)`로 바운드, `TimeoutSentinel`로 `{ok:false,error:"uia_timeout"}`
  구조화 반환. CLI 경로는 orphan thread 가능성을 주석으로 정직하게 명시; **LLM 경로는
  `ct` 전달로 협력적 취소 가능**(WorkspaceTerminalToolHost 배선 확인).
- **엔진 문서 변경** — audio-regression-review를 release-build-pipeline의
  *advisory lane*(hard gate 아님)으로 배선, os-cli-e2e-smoke의 warn-only rubric에
  timeout 근거 추가. 전부 additive, 상태변경 없음.

## 평가 (3축)

| 축 | 등급 | 근거 |
|----|------|------|
| 코드 안전성 | **A** | 0 error, per-instance budget로 무한루프 차단, UIA hang 하드 바운드 |
| 아키텍처 정합성 | **A** | AgentZeroWpf→ZeroCommon 단방향 유지, actor/WPF 경계 위반 없음, 엔진 변경 additive |
| 테스트 가능성 | **B+** | 신규 회귀 5+건 헤드리스 통과. AgentTest(WPF/데스크톱 세션 필요, 엔진 known-hazard)는 이 헤드리스 검증에서 미실행 |

## 머지 승인 판정 → **MERGED**

**APPROVE → operator 승인("승인진행") → 병합 완료.**
빌드 clean · **양 스위트 그린**(ZeroCommon 484 pass, AgentTest 144 pass, 총 0 fail) ·
본문 주장 실측 부합 · 신규 신뢰경계 없음 · 모든 변경 additive.

### 승인 전 최종 게이트 (operator 요청으로 이 환경서 실행)
1. **AgentTest 스위트** — `dotnet test AgentTest -c Debug` → **144 pass / 0 fail / 7 skip
   (151, 13s)**. 데스크톱-의존 known-hazard 게이트 통과 확인.
2. **PR 본문 숫자** — "483" → 실측 484 (baseline 이후 +1, 이상 없음).

### 병합 결과
- 방식: `gh pr merge 12 --merge` (이전 PR #10/#11과 동일한 merge-commit 방식)
- merge commit: `8c1bfc6` — "Merge pull request #12 from psmon/feat/p0-cleanup"
- mergedAt: 2026-08-21T05:10:15Z (UTC) · origin/main 갱신 완료, 로컬 main FF 완료
- feat/p0-cleanup 원격 브랜치는 보존(이전 관행과 동일, 미삭제)

## 다음 단계 제안 (out-of-scope, 별도 이슈 권장)

- **[OOS-1] NU1903 / `System.Security.Cryptography.Xml` 10.0.7 취약점** — High
  severity advisory 다수. 패키지 pin 상향 또는 명시 제거 검토. PR #12와 무관한
  기존 부채이므로 이 PR 머지를 막지 않음. 별도 보안 이슈로 트래킹 권장.
- **[OOS-2 — 정정]** `feat/avalonia-crossplatform`는 **stale 아님**. WPF 종속 Windows
  앱을 크로스플랫폼(Avalonia)으로 전환 시도하는 **의도적 장기 실험 브랜치**이며
  현재 상당수 호환 불가 — **당분간 master 합류 금지**가 정상. drift는 문제 아님.
  registry 등재: `harness/knowledge/_shared/long-lived-branches.md` (pr-review 엔진이
  브랜치 지형 평가 시 이 파일을 먼저 참조하도록 배선함). *초기 리뷰의 "rebase/폐기"
  권고는 operator 정보로 철회.*
- **[OOS-3] `fix/voice-note-loopback-crash`** — main 대비 고유 커밋 0. 이미 반영된
  것으로 보이므로 로컬 브랜치 정리 권장.
