---
date: 2026-04-29T12:04:00+09:00
agent: tamer
type: improvement
mode: log-eval
trigger: "Akka Streams 음성처리 개선 — 자료조사 → 내부 검토 → 구현"
---

# Akka.NET Streams Bidirectional Voice Pipeline — Design Approval

## 실행 요약

User asked the harness to (1) research Akka.NET Streams, (2) review the current
batch voice pipeline, (3) design a stream-based replacement, and (4) execute
the implementation in 4 PR-sized phases.

The garden keeper (tamer) ran a 3-step protocol:

- **Step 1 — Research.** A general-purpose subagent produced a 2,500-word
  technical brief covering Akka.Streams .NET fundamentals, backpressure
  semantics, voice-relevant stages (`GroupedWithin`, `MergeHub`,
  `BroadcastHub`, `KillSwitch`, `RestartSource`, `Source.Queue`,
  `StatefulSelectMany`), actor↔stream bridging, failure handling, and
  Windows/WPF pitfalls. Sources cited inline; JVM-only items flagged.
- **Step 2 — Review of current pipeline.** Confirmed the project ships
  `Akka 1.5.40` + `Akka.DependencyInjection 1.5.40` only; **`Akka.Streams`
  is not installed**. Voice flow (`VoiceCaptureService` →
  `AgentBotWindow.Voice.RunVoicePipelineOnceAsync` → `SendCurrentInput` →
  reactor → batch TTS → `VoicePlaybackService.Play`) is fully batch and
  bypasses the actor system. Identified 9 gaps (G1–G9) and reusable
  assets (`SendStreamAsync` already exists; `TtsTextCleaner.StripMarkdown`
  reusable; dual-VAD logic portable to a `GraphStage`).
- **Step 3 — Design proposal.** Two `RunnableGraph`s (INPUT mic→reactor,
  OUTPUT reactor→speaker) wrapped in a new `VoiceStreamActor` under
  `/user/stage/bot/voice`. 4-step migration roadmap (P0 NuGet bump → P1
  INPUT → P2 OUTPUT → P3 KillSwitch+barge-in+RestartFlow).

## 결과

User approved with three refinements:

1. **Approve overall.**
2. **Actor-mediated control plane.** KillSwitch ownership, STT parallelism,
   and mid-stream interruption must be **actor-mediated**, not implicit
   `.SelectAsync(parallelism: N)` or per-stage `Decider` only. Concretely:
   - `VoiceStreamActor` owns the `SharedKillSwitch` and exposes Tell-only
     commands (`BargeIn` / `CancelInflight` / `EndTurn` / `DeviceLost`).
   - STT calls go through an `SttWorkerPool` actor (round-robin /
     smallest-mailbox router), so parallelism is a runtime config knob.
   - A `BargeInDetector` child actor watches VAD-during-playback events
     and `Tell`s the parent — interruption is a state machine, not a
     filter inside a Flow.
3. **Full P0–P3 in one comprehensive plan**, with **Akka.Streams as the
   single official choice** going forward. Stream graphs (especially
   `GraphDsl.Create()` with explicit `Broadcast` / `Merge` / `BidiFlow`)
   should be leaned on for future complexity (multiple voices, barge-in
   variants, multi-language sessions, multi-output devices).

## 평가 (3축)

| 축 | 등급 | 근거 |
|----|------|------|
| 워크플로우 개선도 | A (제안 시점) | 첫 음절 latency, barge-in, 동시 발화 큐잉, 부분 실패 회복까지 한 모델로 통합. 기존 `_voicePipelineBusy` 드롭-온-비지 패턴이 자연스러운 backpressure로 치환됨. |
| Claude 스킬 활용도 | 3 / 5 | 자료조사는 일반 도구, 구현은 .NET/Akka — 외부 스킬 호출 거의 없음. 실행 단계에서 `simplify`, `harness-view-build` 등이 보조로 들어갈 여지 있음. |
| 하네스 성숙도 영향 | L3 → L3 (중립) | 음성은 코드 도메인. 하네스 구조 자체는 변동 없음. 단, `engine/` 에 `voice-stream-validation` 같은 워크플로우 추가는 P2~P3 즈음 검토 가능. |

## 결정 / 위험 / 비용

- **NuGet 일괄 업**: `Akka` 1.5.40 → **1.5.58+** (조사 결과 v1.5.58이 .NET 10
  CLR 종료-훅 회귀 픽스). 4개 csproj — `ZeroCommon`, `AgentZeroWpf`,
  `ZeroCommon.Tests`, `AgentTest`. `Akka.TestKit.Xunit2`도 함께 동기화.
  `Akka.Streams` + `Akka.Streams.TestKit` 신규 추가.
- **WPF dispatcher 트랩**: 최종 sink만 `synchronized-dispatcher`. 중간 stage가
  UI 스레드 점유 시 데드락. (조사 §9.3, §9.8)
- **GC 압박**: 50 fps × `byte[]` 프레임 → `ArrayPool<byte>.Shared` 도입 필수.
- **JVM-only 함정 회피**: `SourceRef`/`SinkRef`,
  `ActorSource.actorRefWithBackpressure`는 Akka.NET 미지원. 설계에서 제외.
- **전례 부재**: Akka.NET으로 voice 파이프라인 구현한 공개 레퍼런스 없음 →
  본 프로젝트가 첫 사례가 됨. 설계 문서를 `Docs/design/` 에 별도로 보존하는
  것을 P3 종료 시 검토.

## 다음 단계 제안

- [ ] **P0** — Akka 1.5.40 → 1.5.65 일괄 업 + `Akka.Streams` / `.TestKit` 추가.
      빌드 + `dotnet test ZeroCommon.Tests` 그린 + 기존 종료 데드락 무회귀 확인.
- [ ] **P1** — `VoiceStreamActor` + INPUT graph (쉐도우 모드, 행동 변경 X).
      `SttWorkerPool` 라우터 도입. 기존 `OnVoiceUtteranceEnded` 경로와 병행 가동
      후 메트릭 비교 → 합격 시 기존 경로 제거.
- [ ] **P2** — OUTPUT graph + Reactor `IAsyncEnumerable<string>` 노출.
      `progressive TTS` A/B 토글로 첫 음절 발화 latency 측정.
- [ ] **P3** — 액터-매개 KillSwitch + `BargeInDetector` + `RestartFlow.WithBackoff`.
      인위 장애(HTTP 502, 디바이스 분실) 통과 시나리오 테스트.

세부 설계 / 토폴로지 다이어그램 / Decider 정책 / 메시지 프로토콜은 본 로그의
직전 대화 메시지 (Phase 2 + Phase 3 보고)를 정본으로 참조한다. P0 시작.
