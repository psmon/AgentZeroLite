---
date: 2026-07-15T16:08:07+09:00
agent: voice-curator
type: review
mode: log-eval
trigger: "보이스노트 사용중 크래쉬 / 메모리릭 로그 확인 (5분 후 크래시)"
engine: crash-dump-triage (no dump artifact — routed to code-level diagnosis)
---

# Voice-note 크래시 진단 — 루프백 청크 파이프라인 동시성/누수

## 실행 요약

사용자 증상: **보이스노트를 5분 이상 정상 사용하다가 크래시**. 메모리릭 의심.

수집 증거:
- `voice-settings.json` → `InputSource: SystemLoopback`, `SttProvider: WhisperLocal`,
  `SttWhisperModel: medium` (~1.5 GB), `SttUseGpu: true` (Vulkan)
- `diarization-settings.json` → `Provider: SherpaPyannote3D` (화자분리 **ON**)
- 런타임 로그(`…/Programs/AgentZeroLite/logs/app-log.txt`)에는 크래시 세션이
  **없음** — 마지막 기록은 mic 경로의 정상 종료(clean CoordinatedShutdown).
  네이티브 하드 크래시가 로그를 flush 못 하고 죽는 패턴과 정확히 일치.
- 크래시 덤프(`*.stackdump`/`*.dmp`)는 워킹트리·시스템 어디에도 없음
  → crash-dump-triage 엔진의 security-guard/build-doctor 포렌식 단계는 skip,
  코드레벨 진단으로 전환.

## 결과 — 루트 원인

**루프백(SystemLoopback) 경로의 STT+화자분리가 직렬화 없이 무한 fire-and-forget로
실행되며, 그 아래의 whisper.cpp(Vulkan) 팩토리와 단일 Sherpa 디아라이저는
동시 추론에 안전하지 않다.** 5분간은 처리가 실시간을 따라가지만, Whisper medium이
30초 청크를 30초 안에 못 끝내는 순간(CPU/중급 GPU에서 흔함) 작업이 겹치고 —
공유 네이티브 상태에 대한 동시 추론 → 힙 손상 → 몇 분 뒤 네이티브 크래시.
"5분 잘 되다가 크래시"의 정확한 서명.

### 결정적 코드 경로

1. **청크 타이머에 재진입 가드 없음** — `WebDevHost.cs:645 OnNoteChunkTick`
   - 30초마다 주기적으로 발화, 매번 `_ = Task.Run(() => ProcessFinalChunkAsync(...))`
     (line 664) — await도, busy 가드도 없음.
   - 대조: 파셜 타이머 `OnNotePartialTick`(line 561)은 `_notePartialBusy`
     Interlocked 가드로 재진입을 막는다. **청크 타이머만 이 방어가 빠져 있다** —
     이 비대칭이 결함.

2. **파셜 STT와 청크 STT가 서로에 대해 직렬화되지 않음**
   - 파셜(10초)과 청크(30초)가 겹치면 공유 static `WhisperFactory`(Vulkan,
     `WhisperLocalStt.cs:26`)에 대해 **동시** `ProcessAsync` 2건.
   - `SttUseGpu:true` → Vulkan 백엔드. 동일 디바이스에 대한 동시 추론은 큐 충돌로
     크래시하는 잘 알려진 케이스. 실제로 이 클래스는 한 번 SEH나면 heap 오염으로
     FailFast한다고 주석(line 30-34)에 명시 — 즉 이미 취약성을 인지한 코드.

3. **단일 Sherpa 디아라이저 인스턴스에 대한 동시 Process**
   - `ProcessFinalChunkAsync`(line 702)가 공유 `_noteDiarizer._sd.Process()` 호출.
   - `SherpaSpeakerDiarizer`는 `_sd` 하나를 재사용(`SherpaSpeakerDiarizer.cs:24`).
     `OfflineSpeakerDiarization`은 스레드세이프하지 않음 → 겹친 청크 작업이 동시
     Process 시 네이티브 크래시(Whisper와 같은 뿌리).

### 부차 이슈 (누수/효율, 크래시 가속 요인)

4. **Sherpa `Process()` 결과 네이티브 핸들 미해제**
   `SherpaSpeakerDiarizer.cs:94` `raw = _sd.Process(samples)` — 반환된 네이티브
   result를 Dispose하지 않음. 30초마다 1건씩 누적 → 5분이면 10건+ 네이티브 누수.
   `DisposeAsync`(line 128)도 "wrapper에 Dispose 없음, 파이널라이저 의존"으로
   방치. 사용중 바인딩(`org.k2fsa.sherpa.onnx`) 버전 확인 필요.

5. **`_pcmBuffer` 바이트 단위 `List<byte>.Add`** — `LoopbackCaptureService.cs:208`
   16kHz mono에서 초당 32,000회 lock+Add. 크래시 원인은 아니나 GC 압박·CPU 낭비.
   청크 타이머가 매 tick `ConsumePcmBuffer()`로 비우므로 버퍼 자체는 무한증식 아님
   — 단, (1)의 백로그로 청크가 겹치면 드레인 리듬이 깨져 순간 메모리 스파이크.

6. **voice-note.js `openDb()` 매 호출 새 연결** — `voice-note.js:21` 모든
   dbPut/dbAll/dbDelete가 IndexedDB 연결을 새로 열고 닫지 않음. 브라우저측
   경미한 누수(네이티브 앱 크래시와 무관).

## 수정 방향 (권고)

**핵심(반드시):** 노트 STT+디아라이즈 전체를 단일 게이트로 직렬화.
- `WebDevHost`에 `SemaphoreSlim _noteInferGate = new(1,1)` 하나 추가.
  파셜·청크 양쪽의 `TranscribeAsync`+`DiarizeAsync` 구간을 이 게이트로 감싼다
  → 어떤 두 네이티브 추론도 절대 겹치지 않음.
- 청크 타이머에 파셜과 동일한 busy 가드 추가(이미 처리 중이면 그 tick은
  **coalesce/drop 하고 로그**) — 백로그 무한 적재 방지.
- 겹침으로 drop된 청크는 `AppLogger`로 남겨 "조용한 유실"을 가시화.

**부차:**
- (4) 사용중인 sherpa-onnx 바인딩에서 `Process` 결과/`OfflineSpeakerDiarization`이
  `IDisposable`이면 `using`/`Dispose` 연결. 아니면 버전업 추적 이슈 등록.
- (5) `_pcmBuffer`를 `List<byte>` → 사전할당 링/`byte[]` chunked 로 교체(선택).
- (6) voice-note.js에 DB 연결 1회 캐시.

## 평가 (voice-curator 3축 발췌)

| 축 | 측정 | 판정 |
|----|------|------|
| Cross-source consistency | mic는 발화경계로 자연 직렬화되나 loopback 경로는 동시성 가드 부재 — 두 경로가 STT 직렬화 계약을 다르게 취급 | **Fail** |
| UI responsiveness (cold load 비블로킹) | 팩토리 재사용은 OK, 그러나 동시 추론이 UI/프로세스를 죽임 | **Fail** |
| Knowledge capture | 본 로그로 loopback 동시성 계약 문서화 시작 | Pass |

**종합 등급: D (수정 필요, 크래시 재현 경로 명확).**
코드 안전성=위험(네이티브 동시추론), 아키텍처 정합성=경보(파셜/청크 가드 비대칭),
테스트 가능성=`WebDevHost` 노트 파이프라인에 동시성 회귀 테스트 부재.

## 다음 단계 제안

- [ ] `_noteInferGate` 직렬화 패치 + 청크 재진입 가드 (P0)
- [ ] sherpa `Process` 결과 Dispose 여부 바인딩 확인 (P1)
- [ ] `harness/knowledge/voice-curator/`에 "loopback 청크 파이프라인 직렬화 계약"
      지식 문서 추가 (P1)
- [ ] 노트 STT 동시성 회귀 테스트(`AgentTest`) 추가 (P2)
