---
date: 2026-10-30T20:10:00+09:00
agent: voice-curator
type: recruit
mode: recruit
trigger: "네모트론이 안정적이고 빠른듯하네 이것을 공식 채택 영입해죠..아 그리고 이버전 아발로니아가 공식이고 이파트에 적용해죠"
---

# Nemotron 3.5 ASR adopted as the official streaming STT; applied to the Avalonia host

## 실행 요약

Follow-up to `2026-10-30-realtime-streaming-stt-recruit.md`. The operator chose Nemotron as the official model and
named Avalonia as the official host. Recorded the decision in voice-curator's knowledge and brought the feature to
Avalonia: the Voice settings page and a 🎙 in the AgentBot composer, both over ZeroCommon (no WPF edits — the
conversion rule).

### Plan
Make Nemotron the documented, labelled default; give the Avalonia host the streaming provider, its model download,
a live mic test, and AgentBot voice input that reuses the same voice actor + Akka.Streams graph as WPF.

### Do
- ZeroCommon: `PortableVoiceRuntime` (`SherpaStreaming` first, `CreateStreamingStt`, batch adapter for `CreateStt`),
  `Voice/Streaming/VoiceMicSession` (platform mic → `MicFrame` → voice actor), `ModelResidency.WarmVoiceInAsync`
  loads the streaming model too.
- Avalonia: `VoiceSettingsViewModel` (+ picker with *official*, Download, endpoint, 🎙 Listen live) and
  `SettingsView.axaml`; `AgentBotViewModel.Voice.cs` + 🎙 button in `AgentBotView.axaml`.
- Tests: Avalonia `VoiceSettingsViewModelTests` (1) and `AgentBotViewModelTests` (2); ZeroCommon live
  `Mic_session_through_the_voice_actor_sends_one_settled_sentence` (WAV played as a microphone, real model).

### Study
- Avalonia tests 128/128. Live mic-session test: partials 주민등록 → … → final "주민등록증을 보여주시겠어요?".
- `git diff --stat main -- Project/AgentZeroWpf` for this step: no new WPF edits (the earlier WPF wiring is from the
  first step of the same worktree).

### Act
- Knowledge: "Official decision" section + hosts in `streaming-stt-models.md`; README-Avalonia(-KR) rows ✅;
  README(-KR), CLAUDE.md updated; `harness/docs/v1.14.2.md` appended.

## 결과

The Avalonia host offers streaming STT end to end. Not done: a person speaking into a real microphone in the
Avalonia GUI (the path is verified with a WAV-backed microphone through the real actor and model).

## 평가

- 코드 안전성: Good — partials never send; mic-off flushes and tears down; a frame in flight after Stop is ignored
  in Listen live; the 🎙 hides where there is no microphone (macOS / Linux).
- 아키텍처 정합성: Good — Avalonia holds only view-model glue; mic session, runtime and actor are ZeroCommon and shared
  with WPF's actor path; no WPF file changed for the conversion.
- 테스트 가능성: Good — VM handlers are plain methods (tested without a device); the live test replaces the device
  through `IAudioCapturePlatform`.

## 다음 단계 제안

1. Operator: Avalonia Debug build → Settings → Voice → `SherpaStreaming` → Download → 🎙 Listen live, then 🎙 in AgentBot.
2. Portable `IAudioPlaybackQueue` so AgentBot answers can be spoken (+ barge-in) on Avalonia.
3. Phase 2-1 (command intents on partials) — now lands in both hosts at once via the voice actor.
