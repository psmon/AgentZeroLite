---
date: 2026-07-15T17:30:00+09:00
agent: voice-curator
type: review
mode: log-eval
trigger: "방금 또 크래시 / STT vs 화자분리 메모리 방식 비교 후 구현"
linked_logs:
  - harness/logs/voice-curator/2026-07-15-16-08-voice-note-loopback-concurrency-crash.md
  - harness/logs/voice-curator/2026-07-15-16-20-voice-note-infer-gate-p0-patch.md
  - harness/logs/voice-curator/2026-07-15-16-40-diarizer-native-dispose-p1-patch.md
---

# 재크래시 진단 — STT/화자분리 메모리 수명 비대칭 (chunk-scoped 재구현)

## 실행 요약

P0/P1 적용 빌드(Debug)로 재테스트했으나 **또 크래시**. 크래시 로그
(`…/bin/Debug/net10.0-windows/logs/app-log.txt`, 17:11~17:15)를 분석.

## 결과 — P0는 동작, 그러나 다른 루트 원인

- **P0 게이트는 정상 작동 확인** — 크래시 로그에서 utterance가 게이트 보유 중일
  때 partial STT가 정확히 skip됨(17:15:12→17:15:26 구간 partial 없음). 동시
  네이티브 추론은 제거됨. 그런데도 크래시 → 이번 크래시는 **동시성이 아님**.
- **소스가 Microphone**(loopback 아님). mic 경로도 `ProcessFinalChunkAsync`를
  타므로 게이트 적용됨.
- 크래시는 **네이티브 추론 중** 하드 폴트(마지막 331200바이트 STT의 "STT ok"
  없이 로그 절단, 관리 예외 없음).
- 하드웨어: **AMD Radeon 8060S — 통합 GPU**(VRAM=시스템 RAM 공유). Whisper
  medium **Vulkan**(SttUseGpu=true, device=0) + 화자분리 CPU ONNX가 매 발화 실행.
  화자분리 `inferMs`가 6650→7909→8227→8889→10099로 **상승**(자원 압박 누적 신호).
- 로컬 LLM(gemma-4-E4B, GpuLayerCount=999, VulkanDeviceIndex=0)도 동일 iGPU 타깃
  으로 설정됨(사용자의 "LLM 의심"이 방향은 맞음). 단 이 세션엔 LLM 추론 없음.

## 결과 — 사용자 가설 검증 (STT vs 화자분리 메모리 비교)

사용자 지적: "STT-only는 롱텀 안정. STT는 정크단위 분석+인스턴스 메모리 반환
반복. 화자분리 의심." → **코드 비교로 확증됨.**

| | STT (WhisperLocalStt) | 화자분리 (Sherpa) — 기존 |
|---|---|---|
| 무거운 모델 | WhisperFactory: static 1회 로드 공유 | OfflineSpeakerDiarization: 단일 인스턴스 1회 생성 |
| 발화당 추론 객체 | `await using processor` — **매번 생성+Dispose** | `_sd.Process` — **같은 인스턴스 재사용, 리셋 없음** |
| 발화당 네이티브 메모리 | **반환됨** | **반환 안 됨** (ONNX arena가 피크까지 성장 후 OS 미반환) |

STT는 factory(무거움) 상주 + processor(가벼움) 매 호출 재활용 → 발화당 메모리
반환. 화자분리는 인스턴스 하나를 세션 내내 재사용 → ONNX Runtime arena 누적.
iGPU 공유 RAM에서 이 누적 + Whisper Vulkan 할당 → 드라이버 폴트(~5분). `inferMs`
상승이 이를 뒷받침.

## 결과 — 변경 (구현)

`Project/ZeroCommon/Voice/Diarization/SherpaSpeakerDiarizer.cs` 재구성:
- 영속 `_sd` 제거 → **청크 스코프**: `DiarizeAsync`가 매 호출
  `using var sd = new OfflineSpeakerDiarization(BuildConfig(...))` 로 생성→Process
  →Dispose. 발화마다 네이티브 scratch/arena를 OS로 반환(STT의 per-call processor
  dispose와 동형).
- `EnsureReadyAsync`: 영속 인스턴스 대신 모델 파일 검증 + **1회 warm build/dispose**
  (에러 조기 노출 + 디스크 캐시 프라임). 경로만 캐시(`_segPath/_embPath`).
- `BuildConfig()` 추출. `buildMs`/`inferMs` 로깅 추가
  (`[Diar] chunk-scoped | buildMs=… inferMs=… segs=…`) → 재로드 비용 실측용.
- 구조적 사실: Sherpa는 factory/processor 분리가 없어 "청크당 메모리 반환"은 세션
  재생성(~46MB ONNX 재init)을 수반. 비용 과다 시 recycle-every-N으로 국소 전환
  가능(DiarizeAsync 한 곳).

## 검증

- build 경고0/오류0. ZeroCommon.Tests 305통과/0실패.
- 실기기 재테스트 필요: 새 로그 `[Diar] chunk-scoped buildMs/inferMs`로 (a) 재로드
  비용 허용 여부, (b) 5분+ 세션 크래시 미발생 + 메모리 안정 확인.

## 평가

| 축 | 판정 | 비고 |
|----|------|------|
| 코드 안전성 | Pass | 발화당 네이티브 메모리 결정적 반환, 누적 제거 |
| 아키텍처 정합성 | Pass | STT의 검증된 per-call 수명 모델과 정렬 |
| 테스트 가능성 | Warn | 실기기 메모리 안정성은 런타임 로그로만 검증 가능 |

**종합: A- (실기기 검증 대기).** 사용자 가설을 코드 비교로 확증하고 STT와 동일한
메모리 수명 모델로 정렬.

## 다음 단계 제안

- [ ] 실기기 5분+ mic 세션 재테스트 → `[Diar] chunk-scoped` 로그로 buildMs 확인
- [ ] buildMs가 과하면(>1~2s) recycle-every-N 캐시로 전환
- [ ] 여전히 크래시 시: Whisper를 CPU로(SttUseGpu=false) 내려 iGPU Vulkan 경합 제거
      또는 STT small 모델 검토 (2차 방어선)
