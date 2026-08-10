---
date: 2026-08-10T14:00:00+09:00
agent: tamer
type: creation
mode: log-eval
trigger: "orca 참고 영입 — 별도 공간 pull 분석 + 단계별 구현계획"
engine: orca-adoption
phase: P0
---

# orca 영입 — 분석 & 페이즈 계획 수립 (Phase 0)

## 실행 요약

operator 요청: stablyai/orca를 별도 공간에 pull 받아 분석하고, AgentZero의
멀티-CLI(IDE→ADE) 베이스 위에 영입하면 좋은 개발자 지원 기능을 선별해
단계별 구현계획으로 문서화. 하네스는 단계별 진행을 추적.

수행:
1. orca를 shallow clone → 최종 참조본 위치 **`E:\git-other\orca`** (읽기 전용) 확정.
   - scratchpad 임시본은 정리. E: 파일시스템 소유권 미기록 → `git config --global
     --add safe.directory E:/git-other/orca`로 예외 등록.
2. 병렬 Explore 에이전트 2기로 (a) orca 아키텍처/기능, (b) AgentZero 현행 IDE→ADE
   베이스를 동시 분석·대조.
3. 영입 우선순위 표 + 4단계(+P0) 로드맵 도출.

## 결과

- 문서 3종 신규:
  - `Docs/agent-orca/README.md` — 개요·참조위치·우선순위표·페이즈요약·추적규약
  - `Docs/agent-orca/01-orca-feature-catalog.md` — orca 기능 11종 카탈로그(파일경로+이식성)
  - `Docs/agent-orca/02-phase-plan.md` — 페이즈별 태스크 체크리스트
- 엔진 신규: `harness/engine/orca-adoption.md` (agents: tamer/code-coach/build-doctor/test-sentinel)
- `harness.config.json`: engine 배열에 `orca-adoption` 추가, lastUpdated 갱신.

핵심 판정:
- 🟡 확장(이미 유사 자산): App-CLI/IPC, 토큰 텔레메트리, SSH provider, WebView2 브라우저, 승인 UX
- 🔴 신규·고레버리지: 에이전트 훅 상태수신(P1), Diff 주석→재투입(P2), Run/Task/Dispatch 오케스트레이션(P3)
- AgentZero의 Akka 액터 → 오케스트레이션에서 orca보다 구조적으로 유리.

## 평가 (3축)

| 축 | 결과 | 근거 |
|---|---|---|
| 코드 안전성 | A | 이번 단계는 문서/엔진만 — 코드 변경 없음. 향후 trust preset/파일도구는 보안 게이트 명시. |
| 아키텍처 정합성 | Pass | 계획이 단방향 의존·액터 WPF 비의존 규칙을 페이즈마다 전제로 못박음. |
| 테스트 가능성 | A | 각 페이즈 산출물을 ZeroCommon(헤드리스) 우선 배치, 유닛 테스트 명시. |
| 이식 충실도 | Pass | 재사용 아닌 개념 이식 원칙 명문화(구현 TS/Electron). |
| 스코프 규율 | Pass | 고비용 P4(데몬/플러그인)는 개별 승인으로 분리. |

## 다음 단계 제안

- **Phase 1(에이전트 훅 + trust preset) 상세 설계 착수** — 가장 고레버리지.
  진입점: `CliHandler.cs`에 `agent-hook` 서브커맨드, `AgentHookInstaller.cs`,
  `TrustPresetWriter.cs`. orca `agent-trust-presets.ts` 경로 명세 이식.
- P0 잔여: `IAgentToolbelt` 파일 도구(read/write/edit/grep) 추가 타당성 검토
  (오케스트레이션·diff 재투입 전제).
- operator 승인 시 페이즈 트리거: "orca 페이즈 P1 진행해".
