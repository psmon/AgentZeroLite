---
date: 2026-08-10T19:20:00+09:00
agent: code-coach
type: creation
mode: log-eval
trigger: "orca 페이즈 — W6 Run/Task/Dispatch 오케스트레이션"
engine: orca-adoption
phase: W6
---

# W6 — 감독형 오케스트레이션 (Run/Task/Dispatch DAG) 코어 완료

## 실행 요약

단일 에이전트 → **감독형 워커 풀**의 핵심을 구현. 코디네이터가 task DAG를 소유하고
ready task를 워커에 dispatch, `WorkerDone` 신호로 DAG를 전진. orca
`skill-guides/orchestration.md`의 Run/Task/Dispatch 모델을 Akka 액터로 이식.
AgentZero의 기존 액터 시스템 덕에 orca 대비 자연스러운 매핑.

## 결과 (편집 지점)

- `Project/ZeroCommon/Orchestration/OrchestrationDag.cs` **(신규, 순수)** — DAG readiness
  (`ReadyTasks`), 사이클/미지 의존 탐지(`HasCycle`, Kahn), 완료 판정. 헤드리스 테스트 핵심.
- `Project/ZeroCommon/Data/Entities/OrchestrationRun|Task|Dispatch.cs` **(신규 3종)** — Run 1—* Task
  (deps=JSON 배열), Dispatch(task↔worker 배정). `AppDbContext` DbSet 3 + cascade + 인덱스.
- `Project/ZeroCommon/Data/Migrations/20260810112303_AddOrchestration.cs` — `dotnet ef`(빌드 후).
  3 CreateTable 검증.
- `Project/ZeroCommon/Actors/Messages.cs` — 섹션 9: `OrchestrationTaskSpec`, `StartOrchestrationRun`,
  `DispatchTaskToWorker`, `WorkerDone`, `WorkerHeartbeat`, `AskCoordinator`+`Reply`, `Escalation`,
  `QueryRunStatus`+`RunStatusReply`, `OrchestrationRunCompleted`.
- `Project/ZeroCommon/Actors/CoordinatorActor.cs` **(신규)** — `ReceiveActor`. workerRouter(전송
  추상화)로 dispatch, WorkerDone로 전진, 사이클 즉시 abort, 실패 의존은 dependent 블록 + 실패 종료,
  ask=proceed 결정 게이트, heartbeat/status.

## 검증

- `dotnet test --filter Category=Orchestration|CoordinatorActorTests` → **13/13 통과**:
  - DAG(7): no-dep 전체 ready, dep 블록/해제, in-flight·completed 스킵, 사이클 탐지, 미지 의존, AllComplete.
  - Coordinator(6, Akka.TestKit): 선형 체인 순서 dispatch, 독립 task 병렬, **실패 의존 블록+실패 종료**,
    사이클 즉시 abort, ask→proceed, status 스냅샷.
- 초기 1 실패(실패 task가 completed/inFlight에 없어 재-dispatch) → `Excluded()`=inFlight∪failed로 수정 후 통과.
- 전체 회귀: `dotnet test ...ZeroCommon.Tests` → **366 통과, 24 스킵, 0 실패**.
- `dotnet build AgentZeroWpf -c Debug` → **오류 0**.

## 평가 (3축)

| 축 | 결과 | 근거 |
|---|---|---|
| 코드 안전성 | A | 사이클/미지 의존 즉시 abort(데드락 방지). 실패 task 재-dispatch 방지. 메시지 불변 record. |
| 아키텍처 정합성 | Pass | DAG 순수 로직 ZeroCommon(헤드리스). 코디네이터 transport-agnostic(workerRouter 주입) → 격리 검증. Akka 액터 정합. |
| 테스트 가능성 | A | DAG 유닛 + 코디네이터 프로토콜 TestKit 전부 헤드리스. |
| 이식 충실도 | Pass | orca Run/Task/Dispatch/worker_done 모델 이식, C# 액터로 재구현. |
| 스코프 규율 | Pass | 코어(스키마+DAG+코디네이터) 우선. 프로덕션 배선은 follow-up 명시. |

## 다음 단계 제안 (follow-up — 이번 코어 밖)

- **워커 라우터 → 실제 에이전트 배선**: `DispatchTaskToWorker`를 호스팅 터미널/AgentLoopActor에
  연결, 워커가 `WorkerDone`/`Heartbeat` 상신(W1 훅과 연동 가능). WPF 통합 + 실기 검증 필요.
- **`-cli orchestrate`**: Run 생성/Task 등록/status 스크립팅(W4 App-CLI 확장 위에).
- **영속 연동**: 코디네이터 인메모리 상태 ↔ `OrchestrationRun/Task/Dispatch` 저장(재시작 복원).
- **git worktree = 1급 워크스페이스**(3b): `GitWorktreeBuilder` + WorkspaceActor 확장.
- **heartbeat 타임아웃 → escalation**: `IWithTimers`로 stall 감지(현재 heartbeat 기록만).

## 마일스톤

W8·W1·W3·W6 (operator 선택 4종) 코어 구현 완료. 로그 5건, 마이그레이션 2종, 헤드리스 테스트 총
+38(FileTools 18 / AgentHook 17 / GitDiff 7 / Orchestration 13 중 신규분) 추가, 전체 366 통과.
