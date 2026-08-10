---
date: 2026-08-09T09:24:00+09:00
agent: voice-curator
type: review
mode: log-eval
trigger: "오디오 회귀 점검 수행 (via audio-regression-review engine)"
invoked_by: harness/engine/audio-regression-review.md
scope_range: v0.15.0..v0.16.0
linked_logs:
  - harness/logs/voice-curator/2026-07-15-16-08-voice-note-loopback-concurrency-crash.md
  - harness/logs/voice-curator/2026-07-15-16-20-voice-note-infer-gate-p0-patch.md
  - harness/logs/voice-curator/2026-07-15-16-40-diarizer-native-dispose-p1-patch.md
  - harness/logs/voice-curator/2026-07-15-17-30-diarizer-chunk-scoped-memory-model.md
---

# v0.16.0 오디오 회귀 리뷰 — voice 레인 (diarization)

## 실행 요약

`audio-regression-review` 엔진의 첫 실전 실행. 변경 감지 라우팅 결과 voice
레인 발동 (music 레인 no-op). 대상 = v0.15.0..v0.16.0에서 voice-curator watched
path를 건드린 3파일:

- `Project/ZeroCommon/Voice/Diarization/SherpaSpeakerDiarizer.cs` (핵심)
- `Project/AgentZeroWpf/Services/Browser/WebDevHost.cs` (note 파이프라인 + 브리지)
- `Project/AgentZeroWpf/Services/Browser/WebDevHost.Mp3.cs` (mp3 partial)

이 변경들은 이미 7월 15일 4개 로그(linked)로 상세 진단·구현됐다. 이번 리뷰는
**엔진 시점 회귀 확인** — 릴리스에 실제로 들어간 코드가 큐레이터 계약을 지키는지,
그리고 그 검증 결과가 릴리스 전 게이트를 통과할 자격이 있었는지 재판정.

## 리뷰 결과 — 코드 대조

### 청크 스코프 메모리 모델 (SherpaSpeakerDiarizer.cs)
- 영속 `_sd` 제거 → `DiarizeAsync`가 매 호출 `using var sd = new
  OfflineSpeakerDiarization(BuildConfig(...))` 생성→Process→Dispose. 발화당
  네이티브 arena를 OS로 결정적 반환. STT의 per-call processor dispose와 동형.
- `EnsureReadyAsync`는 영속 인스턴스 대신 1회 warm build/dispose + 경로 캐시
  (`_segPath/_embPath`, `_validated`). 에러 조기 노출 + 디스크 캐시 프라임.
- `BuildConfig()` 추출로 warm/chunk 경로가 동일 config 사용 → **모델 페어
  일관**(pyannote-seg-3.0 + 3D-Speaker, 미변경). 입력 계약 16 kHz mono 유지.
- `DisposeAsync`는 이제 no-op에 가까움(영속 핸들 없음) — 누수 표면 제거.

### 동시 추론 직렬화 (WebDevHost.cs, P0)
- `SemaphoreSlim _noteInferGate(1,1)` — 노트 파이프라인 모든 네이티브 추론의
  단일 직렬화 지점. `ProcessFinalChunkAsync`가 mic-utterance / loopback-chunk를
  **균일하게** 커버 → cross-source 코드 경로 분기 없음. 파셜은 non-blocking
  try-acquire로 skip.

## 평가 (voice-curator rubric)

| 축 | 판정 | 근거 |
|----|------|------|
| Model-pair fidelity | **Pass** | seg+emb 페어 미변경, config 단일 소스(BuildConfig), 16 kHz mono 계약 유지 |
| Merge correctness | N/A | 이번 diff는 merge 로직 미변경 — 재평가 대상 아님(직전 A- 유지) |
| Cross-source consistency | **Pass** | mic+loopback 모두 `ProcessFinalChunkAsync`→게이트→chunk-scoped diarizer, source별 fork 없음 |
| UI responsiveness | **Pass** | EnsureReadyAsync/warm/DiarizeAsync 전부 `Task.Run` off-thread. 단 per-chunk 재빌드(~46MB) 지연은 실측 대기 |
| Cross-model extensibility | **Pass** | `ISpeakerDiarizer` 계약 shape 미변경 |
| Knowledge capture | **Fail** | 청크 스코프 메모리 모델 + `_noteInferGate` 직렬화 계약이 로그에만 존재. `harness/knowledge/voice-curator/` 두 파일(6/6 기준)에 미반영 |

**종합: 코드 정확성 축 전원 Pass — v0.16.0 diarization 변경은 건전.**
유일한 Fail은 knowledge-capture(문서 완결성 축, 런타임 정확성 아님).

## 회귀 판정

- 이 릴리스는 **이미 출하됨**(v0.16.0). recommend-block은 tag-hold 개념이라
  소급 적용 무의미 → **advisory**로 판정.
- 만약 이 리뷰가 릴리스 전에 돌았더라도: 유일한 Fail이 correctness가 아닌
  knowledge-capture이므로 엔진 verdict 규칙상 **recommend-block이 아니라
  advisory**였을 것(엔진의 recommend-block 예시는 broken format/wrong pair/
  non-16kHz 같은 correctness Fail에 한정).

## 다음 단계 제안

- [ ] **[필수 후속]** `harness/knowledge/voice-curator/`에 지식 신설 — 청크 스코프
      메모리 수명 모델 + 노트 파이프라인 `_noteInferGate` 직렬화 계약. P0 로그
      (16:20)의 미완 체크박스 "loopback 청크 직렬화 계약 지식화"를 이행.
- [ ] **[열린 검증]** 실기기 5분+ 세션으로 `[Diar] chunk-scoped buildMs/inferMs`
      로그 확인 — buildMs 허용치 + 메모리 안정성. (직전 17:30 로그 A- "실기기
      검증 대기"가 아직 열려 있음)
- [ ] buildMs 과다 시 recycle-every-N 캐시로 국소 전환 검토.
