---
date: 2026-07-15T16:20:00+09:00
agent: voice-curator
type: creation
mode: log-eval
trigger: "패치 진행 (P0 — _noteInferGate 직렬화 + 청크 재진입 가드)"
linked_logs:
  - harness/logs/voice-curator/2026-07-15-16-08-voice-note-loopback-concurrency-crash.md
---

# P0 패치 — 노트 파이프라인 네이티브 추론 직렬화

## 실행 요약

직전 진단 로그가 특정한 루트 원인(루프백 청크 STT+디아라이즈의 무가드
fire-and-forget → 공유 whisper.cpp(Vulkan)/Sherpa 인스턴스 동시 추론 → 네이티브
힙 손상 → 5분 후 크래시)에 대해 P0 수정을 적용.

## 결과 — 변경 파일

`Project/AgentZeroWpf/Services/Browser/WebDevHost.cs` (단일 파일, 빌드 통과)

1. **`SemaphoreSlim _noteInferGate = new(1,1)` 도입** — 노트 파이프라인의 모든
   네이티브 추론(파셜 STT, 청크 STT, 디아라이즈)을 통과시키는 단일 직렬화 지점.
   두 네이티브 추론이 절대 겹치지 않음이 보장됨.
   - `ProcessFinalChunkAsync`: STT+diarize 구간 진입 전 `WaitAsync(ct)`, `finally`
     에서 `Release`. mic-utterance / loopback-chunk 양쪽 호출자를 균일하게 커버.
     취소(STOP)된 WaitAsync는 조용히 반환.
   - `OnNotePartialTick` 내부 Task: `WaitAsync(0)` **비블로킹 try-acquire** — 다른
     추론이 실행 중이면 파셜은 큐잉 없이 skip(파셜은 best-effort 프리뷰).

2. **청크 타이머 재진입 가드 `_noteChunkBusy`** — 파셜의 `_notePartialBusy`와
   대칭. `OnNoteChunkTick` 진입 시 CompareExchange로 이전 청크가 처리 중이면:
   버퍼는 그대로 드레인해 `_pcmBuffer` 무한증식을 막고, 그 청크는 **drop + 로그**
   (`Note-Chunk busy — dropped N bytes`). 처리 완료 시 `finally`로 busy 해제.
   `StartNoteChunkTimer`에서 busy 플래그 리셋(세션 재시작 위생).

## 설계 근거

- Whisper medium이 30초 실시간 창을 못 따라잡을 때(중급 GPU/CPU에서 흔함)의
  백로그를 "drop + 로그"로 처리 → 조용한 유실 없이 크래시/OOM 회피.
- 청크 STT는 게이트를 블로킹 대기(짧은 파셜이 끝나길 기다림), 파셜은 non-blocking
  skip → 백로그가 파셜 쪽에 쌓이지 않음.
- 정확성: 게이트 홀더는 항상 최대 1 → 동시 네이티브 추론 0 보장.

## 검증

- `dotnet build …/AgentZeroWpf.csproj -c Debug` → **경고 0 · 오류 0**.
- 런타임 재현 테스트는 실제 loopback 캡처 세션이 필요(데스크톱 세션) — 미실시.
  기대 로그 시그널: 백로그 시 `[WebDev:Note-Chunk] busy — … dropped` 출현,
  동시 `Note-Partial`/`Note-Chunk` STT 인터리브 소멸.

## 평가

| 축 | 판정 | 비고 |
|----|------|------|
| 코드 안전성 | Pass | 동시 네이티브 추론 제거 — 크래시 재현 경로 차단 |
| 아키텍처 정합성 | Pass | 파셜/청크 가드 비대칭 해소, 단일 직렬화 계약 |
| 테스트 가능성 | Warn | 동시성 회귀 테스트 미추가(P2로 잔류) |

**종합: B+** — 크래시 루트 원인 제거. 잔여 항목(하기)으로 A 승격 가능.

## 다음 단계 제안 (잔여)

- [ ] P1 — sherpa-onnx 바인딩에서 `_sd.Process()` 결과/`OfflineSpeakerDiarization`
      `IDisposable` 여부 확인 후 Dispose 연결 (네이티브 핸들 누수)
- [ ] P1 — `harness/knowledge/voice-curator/`에 "loopback 청크 직렬화 계약" 지식화
- [ ] P2 — `AgentTest`에 노트 STT 동시성 회귀 테스트
- [ ] 실기기 loopback 5분+ 세션으로 크래시 미발생 확인
