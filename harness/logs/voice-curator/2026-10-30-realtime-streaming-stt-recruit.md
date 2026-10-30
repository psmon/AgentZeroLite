---
date: 2026-10-30T18:40:00+09:00
agent: voice-curator
type: recruit
mode: recruit
trigger: "stt를 추가적으로 영입해보자 … 말하는 중에 반응 … akka stream을 연동해 리얼타임을 처리"
---

# Realtime streaming STT — research, recruit into voice-curator, Phase 1 build

## 실행 요약

Worktree `worktree-realtime-stt` (branch from 68e4916). Researched the operator's two named candidates
(Gemma multimodal, Nemotron) plus small streaming recognizers; picked sherpa-onnx streaming transducers
(Nemotron 3.5 ASR streaming 0.6B default, Korean Zipformer light). Recruited the capability into
voice-curator (sherpa-onnx owner) instead of a new agent. Built Phase 1: a streaming INPUT graph in the
voice actor (Akka.Streams `StreamingSttStage`), WPF provider + model download, wearable CLI for shell checks.

### Plan
Replace "VAD hangover → whole-utterance STT" with a recognizer that decodes while the person speaks and
ends the utterance on its own endpoint; show partials; keep the agent loop on finals only.

### Do
- sherpa-onnx 1.10.46 → 1.13.8; `Voice/Streaming/` (interface, catalog, downloader, engine, batch adapter);
  `Voice/Streams/StreamingSttStage.cs`; `VoiceStreamActor` streaming branch + `CreateVoiceStream.StreamingSttFactory/OnPartial`.
- WPF: Settings → Voice → Provider *Streaming* (model picker, Download, endpoint ms); AgentBot partial preview.
- `AgentZeroWearable.exe --download-stream-stt`, `--hear-stream [--realtime]`.
- Tests: `StreamingSttStageTests` (6), `StreamingSttLiveTests` (2, real models), `SherpaDiarizationLiveTests` (1).

### Study
- Paced in real time (Korean clip 2, 6.6 s): Nemotron — full sentence +0.4 s after speech end, final +0.9 s;
  Zipformer — +0.2 s / +1.2 s. Batch path: 1.2–2 s hangover, then Whisper.
- Accuracy on 4 Korean clips: Nemotron 4/4 exact wording (spacing differs in clip 2: "실수중", "다른집"); Zipformer 2 errors
  ("다른 집안과", colloquial "걔는/할려구").
- Three traps found and encoded: Hangul spacing dropped by sherpa (rebuilt from tokens); move of the unpacked
  627 MB encoder denied for seconds (retry); a 3200-byte "50 ms" frame that was really 100 ms (fixed to 1600).
- sherpa's ORT 1.28.2 native now wins the output folder: Supertonic, KittenTTS 2 decoder, diarizer re-run OK.
- Full headless suite: 1354 passed, 0 failed.

### Act
- Knowledge: `harness/knowledge/voice-curator/streaming-stt-models.md`; sherpa pin updated.
- Harness 1.14.1 → 1.14.2 (`harness/docs/v1.14.2.md`).
- Not done: live mic run in the GUI (needs a person speaking), Avalonia conversion (⏳ row added), Phase 2.

## 결과

Phase 1 works end to end headless and from the shell; the GUI path compiles and is wired but was not driven
with a real microphone in this session.

## 평가

- 코드 안전성: Good — the streaming path is opt-in per provider; an uninstalled model falls back to the batch
  path with Whisper Local; partials never start the agent loop. Risk carried: the ORT native swap (verified on
  three ORT consumers, not on Florence-2 / AST).
- 아키텍처 정합성: Good — all logic in ZeroCommon (WPF holds only the picker and the preview), one engine for all
  catalog models, the actor decides per StartListening so no actor recreation is needed.
- 테스트 가능성: Good — scripted-session stage/actor tests run headless; live tests skip cleanly without models;
  `--hear-stream` reproduces timing from a shell.

## 다음 단계 제안

1. Operator runs a live mic session in the WPF AgentBot (Settings → Voice → Streaming → Download → mic on) and
   reports the felt latency / partial quality.
2. Phase 2-1: `VoiceCommandInterceptor` on partials ("그만" → BargeIn before the endpoint).
3. Phase 2-2: semantic endpoint (sentence-final punctuation / endings → 300 ms).
4. Run the Florence-2 and AST plugins once on the new ORT native.
5. Avalonia conversion of the Voice page row.
