---
date: 2026-10-30T02:40:00+09:00
agent: meta:voice-kittentts2
type: improvement
mode: execution
trigger: "agentzero 에 보이스모델 추가된것 설정추가및 선택되면 이용할수 있도록 (웨러블을 포함 공통 이용인터페이스 구현체 적용)"
---

# KittenTTS 2를 AgentZero 음성 공급자로 — 데스크톱·웨어러블 공통 엔진 인터페이스

## 실행 요약

`E:\lib\kittentts2-onnx`의 연구(README에 단계별 측정)를 제품 코드로 옮겼다. KittenTTS 2(1.7B 음성 LM, 한국어 포함)를
Python 없이 앱의 기존 런타임으로 돌린다: LM은 Q8_0 GGUF를 내장 llama.cpp(Vulkan)로, 디코더는 chatterbox-turbo ONNX를
CPU ORT로, 텍스트 정규화는 kitten_text_processing의 FST 런타임을 C#으로 이식. 동시에 데스크톱과 웨어러블이 같은
`IVoiceEngine`으로 말하게 정리했다.

## 결과

- **공통 인터페이스** `Voice/IVoiceEngine` + `VoiceEngines.Create` — `SupertonicVoiceEngine`, `KittenTtsVoiceEngine`(0.8,
  한국어는 Supertonic으로), `KittenTts2VoiceEngine`. `WearableVoice`는 16 kHz ADPCM 변환만 남기고 엔진별 코드 삭제,
  데스크톱은 `VoiceEngineTts`(ITextToSpeech 어댑터), `WearableDiagnostics`도 같은 엔진에 묻는다
- **KittenTTS 2** `Voice/KittenTts2/`: 모델 저장소(manifest + SHA-256), 폴더에서 설치(해시 검증), 합성기(임베딩 입력,
  alpha 스케일, 배치 prefill, 청크 분할·연결), `TextNorm/`(FST 이식). `LlamaSharpLocalLlm.EnsureNative`로 llama.cpp
  네이티브 초기화를 로컬 LLM과 공유
- **설정**: `VoiceSettings.Kitten2Voice/Kitten2Language/Kitten2ModelDir`, 공급자 `KittenTTS2`. Avalonia Voice 화면에
  보이스(manifest 기준)·언어·설치. WPF 미변경(거기서 저장하면 TTS Off — README-Avalonia에 표기)
- **웨어러블 진단**: `--install-kitten2 <folder>`, `--tts KittenTTS2 --kitten2-dir <bundle>`
- **배포 묶음** `kittentts2-agentzero-v1`(26파일, 2.89 GB, NOTICE·라이선스 포함) → OneDrive `aimodel`에 복사.
  공유 링크의 비브라우저 접근은 401/403/accessDenied(개인 OneDrive가 SPO로 이전된 링크) — 스크립트 다운로드 검증 불가
- **검증**
  - 테스트: ZeroCommon 1355 통과(새 13개: 정규화 78/78 Python 일치, 분할/연결, 설치·변조 거부, 엔진 선택), Avalonia 124 통과,
    WPF 빌드 오류 0
  - OneDrive 동기화 폴더 → `--install-kitten2` 5.3초(해시 검증 포함) → 설치본으로 웨어러블 합성(Vulkan)
  - Whisper: "크롬창을 두 번째 화면으로 옮겼어요. 지금 바로 확인해 보세요.", "안녕하세요? 에이전트 제로입니다. 회의는
    3시 반으로 옮겨졌어요.", "The meeting moved to 3.30 p.m. It costs $12.50."
  - 첫 로드 ~16초, LM ~28 tok/s

## 평가

| 축 | 등급 | 근거 |
|---|---|---|
| 코드 안전성 | B | 설치는 manifest 해시 검증·경로 이탈 차단. Stellon Labs 파생물 배포 조건(NOTICE·라이선스) 포함. 상업 사용은 등록 필요 |
| 아키텍처 정합성 | A | 엔진 하나를 데스크톱·웨어러블이 공유, 네이티브 초기화 공유, 모델은 공용 models 루트 |
| 테스트 가능성 | A | 정규화를 Python 출력과 78건 고정, 설치·엔진 선택 헤드리스 테스트, 실기 합성은 Whisper 역변환 |

## 다음 단계 제안

1. 다운로드 경로: OneDrive 공유는 익명 API가 막혀 앱 내 다운로드에 부적합 — 브라우저로 받은 뒤 Install, 또는
   HF/GitHub Release 등 직접 URL 호스팅으로 Download 버튼 추가
2. 한국어 숫자("3시") 읽기 — en 문법이 숫자를 남김. ko 문법 또는 한국어 숫자 전처리 실험
3. 첫 로드 16초 단축(GGUF mmap/디코더 세션 지연 생성), 문장 단위 스트리밍

## 후속 (03:30~) — GitHub Release 다운로드, KittenTTS 0.8 제거, CPU/GPU 선택

- 모델 묶음을 AgentZero GitHub Release `model-kittentts2-v1`(prerelease, Latest 아님)에 올리고 `KittenTts2ModelDownloader`
  추가(이어받기, 파일별 SHA-256). 깨끗한 폴더로 실제 다운로드 8분 57초, 이전 설치본과 바이트 단위 동일
- KittenTTS 0.8(영어 전용) 공급자·코드·테스트·모델 파일(224 MB) 제거 — KittenTTS 2만 유지
- `VoiceSettings.Kitten2Device`(Auto / GPU / CPU) + Avalonia Device 선택 + `--kitten2-device`. CPU를 골라도 Vulkan 장치가
  있으면 Vulkan 네이티브에 0층 오프로드(같은 프로세스의 로컬 LLM이 GPU를 잃지 않게), GPU 로드 실패 시 CPU로 폴백
- 실측(약 6초 한국어, KoreanNewsAnchor): GPU 로드 13.9초 + 합성 약 11초, CPU 로드 12.4초 + 합성 약 20초. 둘 다 Whisper 원문 일치
- 테스트: ZeroCommon 1337 통과(0.8 테스트 19개 삭제, 첫 실행 1건 실패는 3회 재실행에서 재현 안 됨), Avalonia 125 통과

## 후속 (14:00~) — 모델 상주/동적 로드, 설정 테스트가 느린 원인

- 원인: Voice 탭 테스트가 클릭마다 엔진을 만들고 버려 KittenTTS 2 로드(~13초)를 매번 다시 함. 웨어러블은 엔진을 재사용
- `Models/ModelResidency`: 사용 시 로드(항상) · 저장 시 백그라운드 로드+바뀐 음성 해제 · 시작 시 로드(유형별, 기본 off,
  `model-preload.json`, 창 Opened 후 풀에서). 음성 모델은 프로세스당 1회 상주(정적 캐시). AI 모드는 `DeferredAgentLoop`로
  첫 턴에 로컬 LLM 로드(Load 버튼 불필요). 웨어러블 호스트는 시작 시 음성 출력도 비동기 예열. llama.cpp Vulkan 환경 준비를
  `LlmService.PrepareVulkanEnvironment`로 공유(KittenTTS 2가 먼저 로드해도 GPU 선택·bf16 우회 유지)
- 로드 후에도 느린 원인 측정: 디코더 fp16이 ORT 1.23.2 CPU에서 ~7초 → fp32 ~4초. 컨텍스트 3072→2048로 LM 22→24 tok/s
- 묶음 v1 갱신(fp32 디코더, 3.27 GB). 단일 업로드 100 MB+가 반복 실패(tls: bad record MAC) → 40 MB 조각 19개로 게시,
  다운로더가 조각 결합·조각 내부 이어받기. 재시도 루프 실수로 fp16 자산이 먼저 삭제돼 릴리스가 잠시 깨졌다가 복구됨
- 최종 실측(약 6초 한국어, 로드 후): Supertonic 2.8–3.0초, KittenTTS 2 10.5–11.1초(이전 13.5–14.8초). 로드 9.2초
- 테스트: ZeroCommon(새 ModelResidencyTests 6개 + 조각 다운로드 1개) / Avalonia 통과
