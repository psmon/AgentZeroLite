# orca 영입 분석 — AgentZero Lite ADE 로드맵

> 스냅샷: 2026-08-10 · 대상: [stablyai/orca](https://github.com/stablyai/orca)
> 목적: orca(병렬 에이전틱 개발 IDE)의 개발자 지원(ADE) 기능을 AgentZero Lite에
> 선별 영입하기 위한 분석 + 단계별 구현 로드맵.

## 참조 클론 위치 (지속 분석용)

- **`E:\git-other\orca`** — shallow clone. 소스 재조회/심층 분석 전용 참조본.
  프로젝트에 복사하지 않고 이 위치에서만 열람한다. (구현은 TS/Electron이라 재사용
  불가, **아이디어와 명세만** 이식한다.)
- 재조회 커맨드 예: `cd /e/git-other/orca && git log`, `rg <keyword> src/main`

## 문서 구성

| 파일 | 내용 |
|------|------|
| `README.md` | 본 문서 — 개요, 영입 우선순위, 페이즈 요약, 하네스 추적 규약 |
| `01-orca-feature-catalog.md` | orca 기능 카탈로그 (파일 경로 + WPF 이식성 평가) |
| `02-phase-plan.md` | 페이즈별 상세 구현 계획 + 하네스 추적 체크리스트 |

## orca 한 줄 요약

Electron 43 + React 19 기반 **"병렬 에이전틱 개발용 IDE"**. 터미널형 코딩 에이전트
CLI(~30종)를 **각각 독립 git worktree에 띄워 병렬 실행**하고, AI diff를 리뷰하며,
여러 에이전트를 **감독형 워커 풀**로 오케스트레이션한다. AgentZero Lite와 동일 제품군
이나 규모가 훨씬 큼(main 프로세스 ~90개 도메인, IPC 파일 433개, 자체 CLI + 모바일 앱).

**핵심 통찰:** ADE의 본질은 "터미널 멀티플렉서"가 아니라 **에이전트를 감독 가능한
워커 풀로 승격**시키는 것. AgentZero는 이미 Akka 액터 시스템을 보유하므로 이 지점에서
orca보다 구조적으로 유리하다.

## 영입 우선순위 (레버리지 × 비용)

| # | 영입 기능 | AgentZero 현황 | 판정 | Phase |
|---|-----------|----------------|------|-------|
| 1 | 에이전트 훅 기반 상태 수신 (터미널 스크래핑 대체) | 스크래핑(`ApprovalParser`)만 존재 | 🔴 신규·고레버리지 | P1 |
| 2 | Trust preset (각 CLI 신뢰 파일 사전 기록) | 없음 | 🔴 신규 | P1 |
| 3 | Diff 리뷰 + 인라인 주석 → 에이전트 재투입 | diff 파싱 후 버리기만 함 | 🔴 신규 | P2 |
| 4 | App-CLI 확장 + 안티드리프트 스킬 스텁 | `-cli` IPC 12종 **이미 있음** | 🟡 확장 | P2 |
| 5 | 감독형 오케스트레이션 (Run/Task/Dispatch DAG) | 단일 `IAgentLoop`, 1-run-1-cycle | 🔴 신규·최대 차별점 | P3 |
| 6 | git worktree = 1급 워크스페이스 | 워크스페이스=폴더뿐 | 🔴 신규 | P3 |
| 7 | 지속형 out-of-process PTY 데몬 (재시작 생존) | ConPTY가 프로세스 종속 | 🔴 신규·고비용 | P4 |
| 8 | $ 비용 레이어 + 커맨드 팔레트 | 토큰 텔레메트리 **이미 있음** | 🟡 확장·저비용 | P4 |
| 9 | Design Mode (DOM 클릭→프롬프트) | WebDev/WebView2 **이미 있음** | 🟡 확장 | P4 |
| 10 | 스케줄 자동화 / 캡슐형 플러그인 샌드박스 | 없음 | 🔴 신규 | P4 |

🟡 = AgentZero에 유사 자산이 있어 **확장만** 하면 됨 / 🔴 = 신규 도입

## 페이즈 요약

- **Phase 0 — 기반 정비** (~1주): orca 스냅샷 문서화, `IAgentToolbelt` 파일 도구 여지 검토.
- **Phase 1 — 상태 신뢰성** (2~3주, P0): 에이전트 훅 + Trust preset. 스크래핑 → 훅 기반 정확 상태.
- **Phase 2 — Diff 리뷰 & App-CLI** (3~4주, P1): diff 주석→재투입, `-cli` 확장 + 스킬 스텁 주입.
- **Phase 3 — 오케스트레이션** (4~6주, P1): Run/Task/Dispatch를 SQLite+액터로, worktree 워크스페이스.
- **Phase 4 — 차별화 심화** (선택, P2~P3): PTY 데몬, $ 비용/팔레트, Design Mode, 자동화, 플러그인.

## 하네스 추적 규약

이 로드맵은 **단계별로 진행하며 카카시 하네스가 추적**한다.

- **엔진 등록**: `harness/engine/orca-adoption.md` — 페이즈 진행 오케스트레이션.
- **로그**: 각 페이즈 착수/완료 시 `harness/logs/orca-adoption/{yyyy-MM-dd-HH-mm-title}.md`.
- **평가축**: 코드 안전성 / 아키텍처 정합성(액터·의존성 규칙 준수) / 테스트 가능성(헤드리스).
- **진행 상태**는 `02-phase-plan.md`의 체크리스트에 반영하고, 구조 변경 시
  `harness/harness.config.json` 및 `harness/docs/vX.Y.Z.md`를 갱신한다.

## 주요 코드 레퍼런스 (E:\git-other\orca 내)

- `native/windows-cli-launcher/OrcaCliLauncher.cs` — **C#** Windows CLI 런처. 우리가 겪을 문제의 정답지.
- `src/main/agent-trust-presets.ts` — 각 CLI 신뢰 파일 위치 명세.
- `skill-guides/orchestration.md` (406줄) — Run/Task/Dispatch 오케스트레이션 스펙.
- `skill-stubs/` vs `skill-guides/` — 안티드리프트 스킬 스텁 패턴.
- `src/main/daemon/AGENTS.md` — endpoint-ownership 함정(네임드파이프/소켓 데몬 주의).
- `src/main/agent-hooks/`, `src/shared/agent-hook-*` — 에이전트 훅 상태 수신.
