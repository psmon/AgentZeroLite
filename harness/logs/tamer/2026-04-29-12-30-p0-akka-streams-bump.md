---
date: 2026-04-29T12:30:00+09:00
agent: tamer
type: improvement
mode: log-eval
trigger: "P0 — Akka 1.5.40 → 1.5.67 + Akka.Streams 추가"
---

# P0 완료 — NuGet 버전업 & Akka.Streams 도입

## 실행 요약

- 4개 csproj 일괄 버전업: `Akka` / `Akka.DependencyInjection` / `Akka.TestKit.Xunit2`
  모두 **1.5.40 → 1.5.67** (NuGet 최신 stable, 2026-04-26 릴리즈).
- 신규 추가: `Akka.Streams` 1.5.67 (ZeroCommon, AgentZeroWpf),
  `Akka.Streams.TestKit` 1.5.67 (ZeroCommon.Tests, AgentTest).
- 1.5.67 채택 근거: 1.5.66의 `Task.Yield()` 회귀 픽스 (persistence 미사용이라
  영향 없으나, 최신 stable이 안전 선택).
- v1.5.58이 .NET 10 CLR 종료-훅 회귀 픽스를 포함한다는 조사 결과를
  실제 검증 (현 프로젝트 종료 데드락 회귀 없음).

## 결과

- `dotnet build Project/AgentZeroWpf/AgentZeroWpf.csproj -c Debug`: **성공** (0 error,
  기존 미사용 필드 경고 7건만, 신규 경고 0).
- `dotnet test Project/ZeroCommon.Tests/ZeroCommon.Tests.csproj`:
  **88 passed / 0 failed / 0 skipped** (26m 25s — preview SDK NuGet 캐시 미스
  포함). Akka 액터 토폴로지 회귀 없음.

## 변경 파일

```
Project/ZeroCommon/ZeroCommon.csproj         # +Akka.Streams 1.5.67, bump core
Project/AgentZeroWpf/AgentZeroWpf.csproj      # +Akka.Streams 1.5.67, bump core
Project/ZeroCommon.Tests/ZeroCommon.Tests.csproj  # +Akka.Streams.TestKit 1.5.67
Project/AgentTest/AgentTest.csproj            # +Akka.Streams.TestKit 1.5.67
```

## 평가

- 워크플로우 개선도: **유지**. 이번 PR은 의존성만 손댐, 행동 변경 없음.
- Claude 스킬 활용도: 1/5. 단순 NuGet 작업.
- 하네스 성숙도: L3 → L3 변동 없음.

## 다음 단계 (P1)

- [ ] `VoiceStreamActor` 신설 (`/user/stage/bot/voice`)
- [ ] INPUT graph 토폴로지: `Source.Queue` → `VadStage` → `SegmentBuilder`
      → `SttWorkerPool` → `Sink.ActorRefWithAck(reactorOrUiCallback)`
- [ ] Feature flag `VoiceSettings.UseStreamPipeline` (default false) — 기존 batch
      경로와 옵트인 토글로 병행, 검증 후 결합/스왑.
- [ ] `Akka.Streams.TestKit`로 VadStage / SegmentBuilder / SentenceChunker 단위
      테스트 (헤드리스).
