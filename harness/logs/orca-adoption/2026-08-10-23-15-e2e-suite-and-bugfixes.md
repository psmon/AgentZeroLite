---
date: 2026-08-10T23:15:00+09:00
agent: test-runner
type: review
mode: log-eval
trigger: "e2e 테스트 — 파워쉘 창제어+스크린샷 병행 CLI 검증, Test/ 하위 재사용 스위트"
engine: orca-adoption
phase: E2E
---

# E2E 스위트 신설 + E2E가 잡은 실버그 2건 수정

## 실행 요약

`feat/orca` 신규 기능을 실제 앱에 대해 검증하는 재사용 E2E 스위트를 `Test/e2e/`에 신설.
파워쉘로 CLI 상태확인과 창제어/스크린샷을 병행. 기존 `launch-self-smoke.ps1` /
`os-cli-e2e-smoke` 엔진 패턴 일반화.

## 결과 — E2E 스위트 (Test/e2e/)

- `lib/_common.ps1` — exe 해석, `Invoke-Cli`(WinExe 캡처+타임아웃), assert/test 프레임워크,
  GUI 기동 + `-cli os` 스크린샷 헬퍼.
- `cli/test-cli-inproc.ps1` (Tier 1, 무-GUI) — help/worktree/cost/orchestrate 검증.
- `gui/test-gui-smoke.ps1` (Tier 2, GUI) — 기동+창열거+스크린샷+element-tree(warn-only).
- `run-all.ps1` (오케스트레이터, `-SkipGui`), `README.md`(전략), `.gitignore`(_artifacts).

## 결과 — E2E가 발견한 실버그 2건 (수정 완료)

### 버그 1 — `-cli worktree`/모든 async CLI 데드락 (심각)
- 증상: `-cli worktree list`가 무한 hang(redirect 유무 무관).
- 원인: CLI는 `App.OnStartup`(WPF UI 스레드, `DispatcherSynchronizationContext` 보유)에서
  실행. `GitWorktreeBuilder.ListAsync(...).GetAwaiter().GetResult()`의 await 연속이 UI 컨텍스트를
  캡처 → GetResult가 UI 스레드를 블록 → 고전적 async 데드락.
- 수정: `GitWorktreeBuilder`의 모든 await에 `ConfigureAwait(false)` + CLI에서 `Task.Run(...)`로
  UI 컨텍스트 이탈(3 호출). `GitDiffService`도 동일 하드닝.
- 파일: `Project/ZeroCommon/Module/GitWorktreeBuilder.cs`,
  `Project/AgentZeroWpf/CliHandler.cs`, `Project/AgentZeroWpf/Services/GitDiffService.cs`.

### 버그 2 — CLI DB 명령이 미마이그레이션 DB에서 실패
- 증상: `-cli orchestrate list` → `SQLite Error 1: 'no such table: OrchestrationRuns'`.
- 원인: 런타임 DB 마이그레이션은 GUI 기동 시 `InitializeDatabase()`에서만 적용. 신규 빌드로
  GUI를 안 띄우면 CLI DB 명령(cost/orchestrate)이 신규 테이블 없이 쿼리 → 실패.
- 수정: CLI DB 명령 진입 시 `EnsureDbReady()`(idempotent `InitializeDatabase`) 호출.
- 파일: `Project/AgentZeroWpf/CliHandler.cs` (ShowCost, Orchestrate).

## 검증 (실기기)

- **Tier 1**: 7/7 PASS (help·worktree·cost·orchestrate create/status/list).
  - `cost` 라이브 데이터 확인: 58,903 turns, ~$37k 추정(모델별 분해) — W9 실증.
- **Tier 2**: 4/4 PASS. 스크린샷 2880x1704 캡처 → AGENTZERO LITE v0.16.1 정상 렌더,
  ActivityBar(신규 Diff 버튼 포함) UI 무결. element-tree는 방대(WebView2)해 60s warn-only.
- 오케스트레이터 `run-all.ps1 -SkipGui` → E2E PASSED.

## 평가 (3축)

| 축 | 결과 | 근거 |
|---|---|---|
| 코드 안전성 | A | 데드락(전 async CLI 영향)·DB 크래시 2건 근본 수정. E2E는 실제 유저 홈 미오염 설계. |
| 아키텍처 정합성 | Pass | ConfigureAwait/Task.Run로 UI-스레드 규율. CLI DB 자기-초기화. |
| 테스트 가능성 | A | 재사용 3-tier 스위트. CLI tier 헤드리스, GUI tier 스크린샷 아티팩트. |

## 후속 — Tier 3 (GUI↔CLI 연동) 추가 완료

`gui/test-gui-cli-interaction.ps1` 신설 + `run-all.ps1` 편입:
- status / terminal-list / terminal-read IPC 라운드트립 (실기기).
- **W4 `terminal-wait`가 실제 RTX-NOTE 터미널의 idle을 ~1.2s에 감지** — 신규 명령 실환경 실증.
- `terminal-send`는 셸-타이틀 탭에만(에이전트/SSH 세션 비간섭) — 실환경엔 셸 탭 없어 안전 스킵.
- W1 `agent-hook` fire-and-forget 수락.
- 파싱 이슈 1건 발견·대응: `terminal-list`는 사람용 표 + `--- JSON ---` 마커 + JSON 혼합 출력이라
  마커 이후만 추출(제품 정상, 테스트 파서 보정).

**전체 3-tier 실행 결과**: E2E PASSED — 18 체크(T1 7 / T2 4 / T3 7), 0 실패.
(element-tree는 WebView2 포함 방대 트리로 60s warn-only.)

## 다음 단계 제안

- CI에 `run-all.ps1 -SkipGui` 게이트 편입(헤드리스 T1).
- 버그 1은 **모든 async CLI 서브커맨드**에 영향했으므로, 향후 async CLI 추가 시 Task.Run 래핑 규약화.
- 셸 터미널 자동 생성 CLI가 생기면 T3의 send 라운드트립을 무조건 실행 가능.
