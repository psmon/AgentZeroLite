---
date: 2026-08-10T22:00:00+09:00
agent: code-coach
type: creation
mode: log-eval
trigger: "orca 페이즈 — W6 실전화"
engine: orca-adoption
phase: W6-activation
---

# W6 실전화 — 워커 라우팅 + 영속 + -cli orchestrate (코어)

## 실행 요약

W6 코디네이터(테스트됨)를 실동작에 근접시키는 재사용 프리미티브 구현: 워커 라우팅 액터,
영속 스토어, deps 매퍼, CLI 관리. 실제 GUI↔터미널 배선은 데스크톱 검증 필요라 follow-up 명시.

## 결과 (편집 지점)

- `Project/ZeroCommon/Orchestration/OrchestrationMapper.cs` **(신규, 순수)** — deps JSON ↔ List,
  `OrchestrationTask` ↔ `OrchestrationTaskSpec`. 4 테스트.
- `Project/ZeroCommon/Actors/WorkerRouterActor.cs` **(신규)** — DispatchTaskToWorker를 워커 sink에
  round-robin `Forward`(coordinator를 Sender로 보존 → WorkerDone 역라우팅). 2 테스트.
- `Project/ZeroCommon/Orchestration/OrchestrationStore.cs` **(신규)** — CreateRun/LoadSpecs/MarkTask/
  FinishRun (EF, 재시작 복원).
- `Project/AgentZeroWpf/CliHandler.cs` — `orchestrate list|create <file.json>|status <runId>`
  (in-process DB) + usage.

## 검증

- 헤드리스: OrchestrationActivation mapper 4 + WorkerRouter 2 = **6 통과**. 특히 **end-to-end**:
  coordinator→router→probe worker→WorkerDone→다음 dispatch→완주(success) 검증.
- 전체 회귀 **403 통과 / 0 실패**. `dotnet build AgentZeroWpf` → **오류 0**.

## 평가 (3축)

| 축 | 결과 | 근거 |
|---|---|---|
| 코드 안전성 | A | 라우터는 순수 forward(부작용 격리). 스토어 EF 표준. CLI create는 로컬 DB만. |
| 아키텍처 정합성 | Pass | 라우팅/매핑 ZeroCommon. Forward로 Sender 보존(WorkerDone 역라우팅 정합). |
| 테스트 가능성 | A | 라우팅·매핑·end-to-end 코디네이션 전부 헤드리스(probe worker). |
| 이식 충실도 | Pass | orca dispatch/worker 모델 이식. |
| 스코프 규율 | Partial | **실제 워커=터미널 에이전트 배선 미구현**(GUI 바운드) — follow-up. |

## follow-up (GUI 바운드, 데스크톱 검증 필요)

- **워커 sink 어댑터**: DispatchTaskToWorker → 호스팅 터미널에서 에이전트 기동
  (`SendToTerminalAsync` + W1 훅으로 완료 감지 → WorkerDone). ActorSystemManager에 코디네이터/라우터
  스폰.
- **`-cli orchestrate run <runId>`**: GUI 코디네이터에 IPC로 실행 트리거.
- **인메모리↔영속 동기**: 코디네이터 진행 상태를 OrchestrationStore.MarkTask로 반영.
- **heartbeat 타임아웃→escalation**: IWithTimers stall 감지.

## 마일스톤 — orca 영입 2차 배치 완료

W2·W9·W4·W5·W7·W6실전화 (operator 선택 남은 전체) 코어 구현 완료.
1차(W8·W1·W3·W6) + 2차 = 총 10선 + follow-up 중 코어 전부. 헤드리스 테스트 403 통과.
