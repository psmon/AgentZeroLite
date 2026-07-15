---
date: 2026-07-15T16:40:00+09:00
agent: voice-curator
type: creation
mode: log-eval
trigger: "p1 진행 (Sherpa 디아라이저 네이티브 핸들 Dispose 누수)"
linked_logs:
  - harness/logs/voice-curator/2026-07-15-16-08-voice-note-loopback-concurrency-crash.md
  - harness/logs/voice-curator/2026-07-15-16-20-voice-note-infer-gate-p0-patch.md
---

# P1 패치 — Sherpa 디아라이저 네이티브 세션 결정적 해제

## 실행 요약

진단 로그의 부차 이슈 #4("Sherpa Process 결과 30초마다 누적")를 **리플렉션으로
바인딩(`org.k2fsa.sherpa.onnx` 1.10.46)을 실측 검증**하여 정정하고, 진짜 누수를
수정.

## 결과 — 실측 검증 (초기 가정 정정)

net8.0 어셈블리 리플렉션 결과:

- `OfflineSpeakerDiarization`은 **`IDisposable`을 구현** (`public void Dispose()`
  + 파이널라이저 + `Cleanup()`). → 기존 코드 주석 "no public Dispose on the C#
  wrapper (1.10.x)"는 **사실과 다름**.
- `Process(float[])`는 관리 배열 `OfflineSpeakerDiarizationSegment[]` 반환. 별도
  `private ProcessImpl(IntPtr result)` + P/Invoke
  `SherpaOnnxOfflineSpeakerDiarizationDestroyResult` 존재 → **per-call 네이티브
  result는 `Process` 내부에서 해제됨**. 즉 "30초마다 result 누수"라는 초기
  가정은 **오류**였음. 청크당 result 누수는 없다.

**진짜 누수(단일, bounded):** `SherpaSpeakerDiarizer.DisposeAsync`가 `_sd = null`만
하고 `_sd.Dispose()`를 호출하지 않음 → 세션당 하나 캐시되는 네이티브 디아라이저
(segmentation + embedding ONNX 세션, ~50 MB)가 GC 파이널라이저까지 해제 안 됨.
게다가 `WebDevHost.Dispose()`는 `_noteDiarizer`를 아예 건드리지 않음(캐시 유지
목적상 teardown에서 의도적으로 미해제).

## 결과 — 변경 파일 (2개, 빌드+테스트 통과)

1. **`Project/ZeroCommon/Voice/Diarization/SherpaSpeakerDiarizer.cs`**
   - `DisposeAsync`가 `_initLock` 하에 `_sd?.Dispose()` 실제 호출. 잘못된 주석
     정정(리플렉션 검증 근거 명시). Process 인플라이트와의 레이스는 `_initLock`
     으로 차단.
   - **파급효과**: Test 경로(`SettingsPanel.Diarization.cs`)는 이미 여러 곳에서
     `_diarizer.DisposeAsync()`를 호출 중이었으나 그동안 no-op였음 → 이 한 수정으로
     **Test 경로 누수도 소급 해소**. 단일 지점 수정이 모든 호출자 커버.

2. **`Project/AgentZeroWpf/Services/Browser/WebDevHost.cs`**
   - `Dispose()`에 `_noteDiarizer.DisposeAsync()` 호출 추가(`_chatSession`과 동일
     패턴). teardown이 `_noteCts`를 이미 취소한 뒤라 `_sd` 대상 Process 인플라이트
     없음.

## 검증

- `dotnet build -c Debug` → 경고 0 · 오류 0
- `dotnet test ZeroCommon.Tests` → **305 통과 · 0 실패 · 25 skip**(네이티브/LLM 의존)
- 네이티브 디아라이저 해제는 실제 모델 로드 세션에서만 관측 가능(데스크톱) — 코드
  경로는 `_sd == null`일 때 안전(lock 후 null 대입만).

## 평가

| 축 | 판정 | 비고 |
|----|------|------|
| 코드 안전성 | Pass | 네이티브 세션 결정적 해제, 레이스 가드 |
| 아키텍처 정합성 | Pass | 단일 지점 수정이 모든 diarizer 호출자에 적용 |
| 테스트 가능성 | Warn | 네이티브 해제 자체는 헤드리스 미검증(잔류) |

**종합: A-** — 초기 가정을 실측으로 정정한 뒤 정확한 누수만 최소 수정.

## 잔여 (P2)

- [ ] `AgentTest`에 노트 STT 동시성 회귀 테스트(P0 검증)
- [ ] 실기기 loopback 5분+ 세션: 크래시 미발생 + 프로세스 메모리 안정 확인
- [ ] `harness/knowledge/voice-curator/`에 "loopback 청크 직렬화 + diarizer 수명"
      계약 지식화
