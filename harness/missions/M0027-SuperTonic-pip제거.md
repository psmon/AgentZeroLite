---
id: M0027
title: SuperTonic pip 의존 제거 (네이티브 ONNX 전환)
operator: psmon
language: ko
status: done
priority: medium
created: 2026-07-01
depends_on: M0020
---

# 요청 (Brief)

온디바이스 모델 6종 중 유일하게 pip/Python에 의존하던 SuperTonic TTS(M0020)를
pip 없이 런타임에서 실행 가능한지 검토하고, 가능하면 pip 의존을 완전히 제거한다.

# 조사 결론

SuperTonic은 공식적으로 "running natively via ONNX"이며 Supertone이 **공식 C#
참조 구현**(`csharp/ExampleONNX.cs` + `Helper.cs`, MIT)을 제공한다. 결정적으로
TTS 파이프라인의 최대 리스크인 phonemization이 espeak-ng/g2p가 아니라 단순
`unicode_indexer.json`(유니코드→인덱스 매핑) 이라 순수 C# 포팅이 가능하다.

- HF repo `Supertone/supertonic-3`: onnx 4개(text_encoder / duration_predictor /
  vector_estimator / vocoder) + 데이터 2개(tts.json / unicode_indexer.json) +
  voice_styles 10개 = ~398 MB
- 출력 44.1 kHz 16-bit mono WAV, 31개 언어
- 공식 C# 스택: `Microsoft.ML.OnnxRuntime` + `System.Text.Json` — 프로젝트가 이미
  보유 → 신규 NuGet 불필요

# 결정 (operator)

- **완전 교체**: 파이썬 `SuperTonicTts` + `PythonDiscovery` + `SuperTonicProgressParser`
  제거, 네이티브 `SuperTonicOnnxTts`로 대체
- **모델 확보**: 설정 화면에서 다운로드(기존 AST/diarization 패턴), 미존재 시
  "설정에서 다운로드" 안내. 저장 경로 `%LOCALAPPDATA%\AgentZeroLite\models\supertonic`

## Acceptance

- [x] pip/Python 코드 완전 제거 (SuperTonicTts / PythonDiscovery / ProgressParser + 테스트)
- [x] `SuperTonicOnnxTts : ITextToSpeech` — ONNX Runtime 직접 추론 (`Project/ZeroCommon/Voice/`)
- [x] 텍스트 정규화 / unicode-indexer 토크나이즈 / flow-matching / vocoder / WAV 포팅
- [x] `SuperTonicModelStore` + `SuperTonicModelDownloader` (HF 런타임 다운로드)
- [x] `VoiceSettings` 필드 전환 (PythonPath 제거 → ModelDir/Voice/Speed 추가)
- [x] Settings Voice 탭 재작업 (파이썬 피커/프로브 제거 → Voice/Download/Speed)
- [x] 미존재 시 "설정에서 다운로드" 안내 예외
- [x] 헤드리스 단위 테스트 (정규화 / 토크나이즈 / WAV / 모델 store) — 25개 통과
- [x] `ZeroCommon` + `AgentZeroWpf` 빌드 0 오류, 전체 테스트 275 통과

# 남은 작업 (수동)

- E2E: GUI Voice 탭 → Supertonic 선택 → Download Model → 한국어 합성/재생 확인
  (Python 미설치 환경에서 동작해야 함)
- 참조 대조: 대표 문장을 파이썬 supertonic 출력과 파형/길이 비교 (포팅 정확도)
