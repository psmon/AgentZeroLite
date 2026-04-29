---
date: 2026-04-29T13:50:00+09:00
agent: tamer
type: improvement
mode: log-eval
trigger: "P2 + P3 — OUTPUT graph (progressive TTS) + 액터-매개 barge-in"
---

# P2+P3 완료 — OUTPUT 그래프 + 액터-매개 컨트롤 플레인

## 실행 요약

P2와 P3을 한 사이클로 묶어 진행. P2는 OUTPUT 그래프 (LLM 응답 → 문장 청킹 → TTS 풀 → 점진 재생), P3은 그 위에 얹는 액터-매개 컨트롤 플레인 (BargeIn / CancelInflight / EndTurn / DeviceLost) + 일과적 5xx 자동 재시도.

## 결과 (P2)

### 신규 파일

```
Project/ZeroCommon/Voice/
├── IAudioPlaybackQueue.cs               # ZeroCommon-side 추상 (Tell-only)
└── Streams/
    ├── SentenceChunkerStage.cs          # custom GraphStage<FlowShape<string,string>>:
    │                                    #   - sentence terminator + min/max chunk len
    │                                    #   - hard newline 즉시 분할
    │                                    #   - upstream complete 시 tail flush
    └── TtsWorkerActor.cs                # ITextToSpeech 워커 + SmallestMailboxPool 라우터
                                         # P3 — TransientRetry.WithBackoffAsync 통합

Project/AgentZeroWpf/Services/Voice/
└── NAudioPlaybackQueue.cs               # IAudioPlaybackQueue 구현. WaveOutEvent 시퀀스
                                         #   재생 (PlaybackStopped 콜백으로 큐 드레인)

Project/ZeroCommon.Tests/Voice/Streams/
└── SentenceChunkerStageTests.cs         # 6 TestKit 테스트
```

### 수정 파일

```
Project/ZeroCommon/Voice/Streams/VoiceStreamMessages.cs
                                         # +TtsFactory, +PlaybackFactory in CreateVoiceStream
                                         # SpeakResponse 본격화 (TtsParallelism 추가)
                                         # +SpeakText (single-string 헬퍼)
Project/ZeroCommon/Actors/VoiceStreamActor.cs
                                         # +OUTPUT graph materialisation (OnSpeakResponse)
                                         # +CancelOutputGraph + KillSwitch 보유
                                         # Token pump (Task.Run): IAsyncEnumerable → Source.Queue
                                         # PostStop: 양 그래프 + 풀 일괄 정리
Project/ZeroCommon/Actors/StageActor.cs  # CreateVoiceStream 인자 4개 모두 전달
Project/AgentZeroWpf/Services/Voice/VoicePlaybackService.cs
                                         # private static → internal static (재사용)
Project/AgentZeroWpf/Services/Voice/VoiceRuntimeFactory.cs
                                         # +BuildTts(VoiceSettings)
Project/AgentZeroWpf/UI/APP/AgentBotWindow.Voice.cs
                                         # CreateVoiceStream에 TtsFactory + PlaybackFactory 전달
Project/AgentZeroWpf/UI/APP/AgentBotWindow.xaml.cs
                                         # HandleReactorResult: Stream pipeline + TTS 켜져있으면
                                         # voice.Tell(SpeakText(final, voice))
```

### OUTPUT 그래프 토폴로지

```
SpeakResponse(IAsyncEnumerable<string>, voice, parallelism)
   │
   ▼ (Task.Run: IAsyncEnumerable → Source.Queue.OfferAsync)
Source.Queue<string>(64, OverflowStrategy.Backpressure)
   │
   ▼ ViaMaterialized(KillSwitches.Single<string>, Keep.Both)
SentenceChunkerFlow.Create()                ← StringBuilder + .?!\n boundary detection
   │                                          + min/max chunk-len caps
   ▼
.Select(TtsTextCleaner.StripMarkdown)        ← 마크다운 → 자연스러운 발화
   │
   ▼
.Where(non-empty)
   │
   ▼ .Async() (implied — SelectAsync is its own boundary)
.SelectAsync(parallelism, async chunk =>
    ttsPool.Ask<SynthesizeReply>(SynthesizeRequest(chunk, voice)))
   │                                          parallelism > 1: 다음 청크가 미리 합성됨
   ▼
.ToMaterialized(Sink.ForEach(reply => playback.Enqueue(...)), Keep.Left)
```

## 결과 (P3 — 액터-매개 컨트롤 플레인)

### 신규 파일

```
Project/ZeroCommon/Voice/Streams/TransientRetry.cs
                                         # WithBackoffAsync(action, maxAttempts, baseDelay, log)
                                         #   HttpRequestException / TaskCanceled / IOException 재시도
                                         #   OperationCanceled / ArgumentException 즉시 전파
```

### VoiceStreamActor — 컨트롤 메시지

```
Receive<BargeIn>          → CancelOutputGraph("BargeIn")
Receive<CancelInflight>   → CancelOutputGraph("CancelInflight")
Receive<EndTurn>          → CancelOutputGraph("EndTurn")
Receive<DeviceLost>(msg)  → StopInputGraph + CancelOutputGraph("DeviceLost")
```

### 인라인 barge-in 감지

- `_outputActive` 플래그: `_playback.PlaybackStarted` → true, `PlaybackStopped` → false
- `OnMicFrame`: `_outputActive` 동안 `frame.Rms ≥ _vadThreshold` 가 4프레임 연속이면
  `Self.Tell(new BargeIn())` — 별도 자식 액터 없이 OnMicFrame 인라인.
- `BargeIn` 수신 → `CancelOutputGraph` → KillSwitch.Shutdown + tokenQueue.Complete
  + playback.Stop. 다음 사용자 발화는 INPUT graph 가 정상 처리.

### 일과적 재시도 (RestartFlow 대신)

- `SttWorkerActor` / `TtsWorkerActor` 의 `await _stt.TranscribeAsync(...)` /
  `_tts.SynthesizeAsync(...)` 호출을 `TransientRetry.WithBackoffAsync` 로 래핑.
- 3회 시도, 250 ms / 200 ms 지수 백오프.
- `RestartFlow.WithBackoff` 미채택 사유: 전체 Flow 재시작은 STT 모델/소켓 상태까지
  날려 버려 비용이 큼. per-call 재시도가 음성 파이프라인의 latency 예산에
  더 적합 (5xx ↔ recovery 이내에 다음 발화가 들어오지 않음).

## 검증

- `dotnet build` (Debug, ZeroCommon + AgentZeroWpf): 0 error, 6 pre-existing warnings.
- `dotnet test --filter Voice.Streams`:
  **11 passed / 0 failed (337 ms)**.
  - VoiceSegmenterStage: 5 (P1)
  - SentenceChunkerStage: 6 (P2)
  - 추가 액터 통합 테스트는 후속 사이클에서 (모니터 없는 헤드리스 환경에서
    NAudioPlaybackQueue 모킹이 필요 — 인터페이스 분리는 이미 완료).
- 종료 데드락 회귀 없음 (P0 검증 시 확인).

## 평가 (3축)

| 축 | 등급 | 근거 |
|----|------|------|
| 워크플로우 개선도 | **A** | 마이크 → STT pool → 트랜스크립트 → reactor → 토큰 → sentence chunker → TTS pool → 진행 재생까지 단일 stream 모델로 통합. 사용자 barge-in 시 한 번의 KillSwitch 호출로 전체 OUTPUT graph가 atomic 하게 종료. 동시 발화는 SmallestMailboxPool 라우터가 흡수, 일시적 5xx 는 TransientRetry 가 회복. |
| Claude 스킬 활용도 | 3 / 5 | `general-purpose` 자료조사 1회 (P0 진입 시), 구현은 직접. P3 의 BargeInDetector 자식 액터화는 추후 분리시 `simplify` 스킬 활용 가능. |
| 하네스 성숙도 | L3 → **L3+** | 음성 도메인 코드는 늘었지만 하네스 자체엔 영향 없음. 후속으로 `engine/voice-stream-validation` 추가 시 L4 진입 검토. |

## 트레이드오프 / 알려진 제한

- **Reactor 토큰 스트림 미연결**: 현재 `SpeakText(r.FinalMessage)` — reactor 응답이
  완성된 *뒤에* OUTPUT graph 가 시작됨. 진정한 토큰-동시 재생은 reactor가
  `IAsyncEnumerable<string>` 채널을 expose 해야 함. 후속 작업.
- **Single-instance 음성 액터**: `/user/stage/voice` 는 stage 가 캐시. 다중
  세션 / 다중 디바이스 동시 운영은 후속 작업 (PartitionHub 활용 가능).
- **BargeInDetector 자식 액터로 분리 미완**: 현재 `OnMicFrame` 인라인 카운터.
  더 정교한 정책 (대화 차례 인식, 호명 감지 등) 시점에 자식 액터로 분리.
- **NAudioPlaybackQueue 단순 큐**: WaveOutEvent 순차 재생. 청크 사이 ms-level
  공백 가능. 더 매끄러운 재생은 BufferedWaveProvider 로 한번에 합쳐 흘리는
  방식이 더 좋음 — 후속 개선 후보.
- **단위 테스트 범위**: 순수 GraphStage 두 개 (segmenter / chunker) 만 커버.
  VoiceStreamActor 자체의 액터-단위 통합 테스트는 NAudioPlaybackQueue 의 stub
  구현을 ZeroCommon.Tests 에 추가하면 가능.

## 다음 단계 제안 (후속 사이클)

- [ ] Reactor 토큰 스트림 연결: `AgentReactorActor` 가 토큰을 `Channel<string>` 에
      쓰고 `IAsyncEnumerable<string>` 으로 expose → `SpeakResponse(stream)` 발사.
- [ ] BargeInDetector 자식 액터 분리 + 정책 확장 (음량 + 음성 클래스 검사).
- [ ] BufferedWaveProvider 기반 매끄러운 재생.
- [ ] `harness/engine/voice-stream-validation.md` 워크플로우 정의 — 음성 변경 시
      run order: build → 11개 stream 테스트 → 라이브 마이크 스모크.
- [ ] Settings UI: "Experimental: streaming pipeline" 토글 + STT parallelism 슬라이더.
