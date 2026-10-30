---
date: 2026-10-30T00:00:00+09:00
agent: voice-curator
type: explanation
mode: execution
trigger: "AgentZero에서 제공하는 음성모델을 요약 정리해죠"
---

# Voice model inventory summary (STT / TTS / diarization)

## 실행 요약

Read `ZeroCommon/Voice` (VoiceSettings provider names, WhisperModelStore, SuperTonicModelStore,
OpenAiTts/OpenAiWhisperStt, Diarization/*), the Avalonia `VoiceSettingsViewModel` picker lists and
CLAUDE.md's KittenTTS 2 section, and summarised every voice model the product offers.

## 결과

| Kind | Provider | Model / runtime | Size | Hosts |
|---|---|---|---|---|
| STT | WhisperLocal | Whisper.net GGML tiny / small (default) / medium; Vulkan → CPU | ~70 MB / ~466 MB / ~1.5 GB | WPF, Avalonia, wearable |
| STT | OpenAIWhisper | cloud `whisper-1` | — | WPF, Avalonia, wearable |
| STT | LocalGemma | audio-capable Gemma GGUF on the bundled llama.dll | LLM | WPF only (Avalonia keeps it as a stored-choice stub) |
| TTS | WindowsTTS | SAPI (System.Speech) | — | WPF only |
| TTS | OpenAITTS | cloud `tts-1`, 11 voices | — | WPF, Avalonia, wearable |
| TTS | Supertonic | Supertone ONNX on ORT, 10 voices (M1–M5, F1–F5), 31 langs incl. ko, 44.1 kHz | ~398 MB | all |
| TTS | KittenTTS2 | 1.7 B speech LM Q8_0 GGUF (llama.cpp Vulkan) + chatterbox-turbo fp32 ONNX decoder (CPU) | ~3.3 GB | Windows, all hosts |
| Diarization | Sherpa-ONNX | pyannote-segmentation-3-0 (~6 MB) + 3D-Speaker ERes2Net (~40 MB), CPU | ~46 MB | Voice test / voice-note plugin |

Residency: on use always; on Save the chosen voice/ear preload; at startup only via `model-preload.json` (off by default).

## 평가

- 코드 안전성: N/A (read-only explanation; no code changed)
- 아키텍처 정합성: Good — one `IVoiceEngine` seam for on-device TTS (Supertonic, KittenTTS 2) shared by desktop and watch; host-specific providers (SAPI, LocalGemma) are fenced and kept in the shared settings file without being silently switched.
- 테스트 가능성: Good — WhisperModelStore / KittenTTS 2 TextNorm / Avalonia VoiceSettingsViewModel have headless tests.

## 다음 단계 제안

- voice-curator's knowledge is diarization-only; a `voice-curator/voice-model-catalog.md` with the table above would let the next question be answered without re-reading the code.
