---
date: 2026-10-29T21:30:00+09:00
agent: meta:voice-kittentts
type: improvement
mode: execution
trigger: "음성모델에 KittenTTS도 채택해 TTS 온디바이스에 이용할수 있게"
---

# KittenTTS를 온디바이스 TTS로 채택

## 실행 요약

https://github.com/KittenML/KittenTTS 를 읽고 .NET에서 바로 돌릴 수 있는 쪽을 골랐다.
KittenTTS 2(1.7B, 한국어 포함 20개 언어)는 PyTorch/vLLM/GGUF 전용이라 ONNX 그래프가 없고,
0.8 ONNX 계열(nano/micro/mini, 15–80M, Apache-2.0)은 그래프 하나에 CPU로 돈다. 다만 **영어 전용**
(en-us 음소로 학습)이고 음소 변환에 eSpeak NG가 필요하다. 0.8 계열을 Supertonic 옆의 새 공급자로 붙였다.

## 결과

- **ZeroCommon**: `KittenTtsEngine`(`kittentts_legacy/onnx_model.py` 이식 — StyleTTS2 기호표, 400자 문장 분할,
  `.npz` 보이스 리더, 마지막 5000 샘플 잘라냄), `EspeakNg`(NativeLibrary, phonemizer처럼 문장부호를 빼고 복원),
  `KittenTtsModelStore`/`Downloader`(모델은 HF, eSpeak NG는 PyPI `espeakng_loader` 0.2.4 휠 — SHA-256 고정),
  `KittenOnnxTts`, `VoiceSettings.Kitten*`, `PortableVoiceRuntime`에 공급자 추가
- **웨어러블**: `WearableVoice`가 KittenTTS로 말한다. 한글·가나·한자가 든 답은 Supertonic이 설치돼 있으면 그쪽으로,
  없으면 합성 실패(답은 텍스트로). `--tts KittenTTS` 일회성 옵션 추가
- **Avalonia**: Settings → Voice에 KittenTTS(모델·보이스·속도·다운로드). WPF는 건드리지 않음
- **측정** (같은 영어 문장 약 6초, 이 PC CPU, Whisper small로 역변환):

  | 모델 | 합성 | RTF | Whisper가 들은 것 |
  |---|---|---|---|
  | nano (fp32) | 355 ms | 0.06 | 원문 그대로 |
  | nano int8 | 1334 ms | 0.21 | 원문 그대로 |
  | micro (기본) | 1450 ms | 0.24 | 원문 그대로 |
  | mini | 2102 ms | 0.40 | "sunny" → "Suni" |

- 웨어러블 `--speak` (16 kHz ADPCM 경로): 영어 → KittenTTS, Whisper 일치. 한국어 → Supertonic 폴백, Whisper 일치
- 실측으로 고친 것: 휠을 옆에 풀고 폴더 이름을 바꾸는 방식이 "access denied"(백신이 막 풀린 DLL을 잡고 있음) →
  제자리에 풀고 `.installed` 표시 파일을 마지막에 쓰는 방식으로 변경
- 테스트: ZeroCommon 1342 통과(새 19개, 라이브 1개는 `AGENTZERO_KITTEN_LIVE=1`일 때만), Avalonia 124 통과, 세 호스트 빌드 오류 0

## 평가

| 축 | 등급 | 근거 |
|---|---|---|
| 코드 안전성 | B | 네이티브 DLL을 받아 로드하므로 해시 고정 + zip-slip 차단. eSpeak NG는 GPL-3.0 — 설치 파일에 싣지 않고 사용자가 받는다 |
| 아키텍처 정합성 | A | Supertonic과 같은 모델 루트·다운로드 계약·공급자 팩토리. 두 호스트와 웨어러블이 같은 설정 파일을 읽는다 |
| 테스트 가능성 | A | 기호표를 인덱스 단위로 고정, 라이브 테스트가 다운로드→합성→STT 역변환까지 확인 |

## 다음 단계 제안

1. WPF 음성 화면에 KittenTTS 추가(지금은 거기서 저장하면 TTS가 Off로 바뀐다)
2. 기본 모델 재검토 — nano fp32가 가장 빠르고 인식도 같았다. 음질은 사람이 들어 보고 정하기
3. 숫자·단위 정규화(`preprocess.py`의 TextPreprocessor)는 이식하지 않았다 — 지금은 eSpeak가 숫자를 읽는다
