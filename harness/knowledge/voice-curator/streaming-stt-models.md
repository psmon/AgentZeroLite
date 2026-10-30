# Streaming STT — model selection, serving contract, and the road to "react while speaking"

Owner: voice-curator. Snapshot: 2026-10-30 (worktree `worktree-realtime-stt`).
Code: `Project/ZeroCommon/Voice/Streaming/`, `Project/ZeroCommon/Voice/Streams/StreamingSttStage.cs`,
`Project/ZeroCommon/Actors/VoiceStreamActor.cs` (streaming branch).

## Official decision (operator, 2026-10-30)

**Nemotron 3.5 ASR streaming 0.6B, 560 ms chunk, is the official streaming STT** — "stable and fast" after the
measurements below. It is `StreamingSttModelCatalog.Default`, first in the pickers and labelled *official*; the
streaming provider is first in Avalonia's STT list. The Korean Zipformer stays in the catalog as the lighter
alternative (and its archive carries the Korean test clips the live tests use). A different default needs this
section rewritten with new measurements.

**Host:** Avalonia is the official host — the feature ships there (Settings → Voice, AgentBot 🎙). WPF keeps its
own wiring as the reference build.

## Why this exists

The batch voice path waits twice before anything reaches the agent:

1. **VAD hangover** — the utterance ends only after 24 (Loose) / 40 (Strict) silent 50 ms frames:
   1.2–2.0 s of silence (`VoiceSensitivityProfile`).
2. **Whole-utterance STT** — Whisper then transcribes the full segment (seconds on CPU for `small`).

A streaming recognizer removes both: it decodes as the audio arrives (partials), and its own endpoint rule
closes the utterance after a shorter silence — with the text already decoded.

## Candidates evaluated (2026-10-30)

| Candidate | Kind | Korean | Streaming? | Verdict |
|---|---|---|---|---|
| **Gemma 4 E2B / E4B** (audio in) | multimodal LLM | trained multilingual ASR; no published Korean CER | **No** — one clip ≤ 30 s, 25 tokens per second of audio; llama.cpp audio support landed late and is unstable (E2B Q8_0 stray reasoning tokens → HTTP 500 reported) | Rejected for the ear. Chunking it is still "hear it all, then answer". Keep it for *understanding* audio, not transcribing it live. |
| **Nemotron 3 Nano Omni** (30B-A3B, Parakeet-TDT-0.6B audio) | multimodal LLM | not documented | No (offline clips up to 20 min) | Rejected — 30B total, no Korean statement. |
| **Nemotron 3.5 ASR streaming 0.6B** (`nvidia/nemotron-3.5-asr-streaming-0.6b`) | cache-aware FastConformer-RNNT | ko-KR in the "transcription-ready" tier; FLEURS CER 7.59 / 7.70 / 7.27 / 7.18 / 7.12 % at 80 / 160 / 320 / 560 / 1120 ms (lang given) | **Yes**, chunk 80 ms–1.12 s | **Adopted (default, 560 ms).** Punctuation built in, 40 locales (Korean + English code-switching), OpenMDW-1.1. sherpa-onnx ≥ 1.13.3 (PR #3671). |
| **Korean streaming Zipformer** (`sherpa-onnx-streaming-zipformer-korean-2024-06-16`) | Zipformer transducer | Korean only | **Yes** | **Adopted (light option).** int8 encoder 121 MB, RTF ~0.1 single thread (k2-fsa). No punctuation except a trailing period. |
| kangkyu `icefall-asr-ko-streaming-zipformer-174m` / `-72m` | Zipformer transducer | KsponSpeech CER 7.5–8.3 % (chunk 64→16) | Yes | Not packaged by k2-fsa (no tarball); candidate if the official Korean model disappoints on spontaneous speech. Apache-2.0. |

Sources: HF model card `nvidia/nemotron-3.5-asr-streaming-0.6b`; k2-fsa docs `sherpa/onnx/nemo/nemotron-streaming.html`
and `pretrained_models/online-transducer/zipformer-transducer-models.html`; Google `ai.google.dev/gemma/docs/capabilities/audio`;
HF `kangkyu/icefall-asr-ko-streaming-zipformer-174m`.

## Serving contract (what the code relies on)

- **One engine for every entry**: all catalog models are sherpa-onnx streaming transducers (encoder / decoder / joiner +
  `tokens.txt`). Files are picked by role (`StreamingSttModelCatalog.FindFiles`): encoder + joiner prefer int8, the
  decoder prefers fp32 when the archive ships it (k2-fsa's own int8 examples do this).
- **Language** (Nemotron only): `OnlineStream.SetOption("language", "ko")`; empty / `auto` → the model's own detection.
  Zipformer ignores it.
- **Input**: 16 kHz mono float. The mic frame is 50 ms = **1600 bytes** of PCM16. (An earlier draft assumed 3200 —
  that is 100 ms and doubled every timing printed.)
- **No VAD in front.** Endpoint rule 2 counts trailing silence frames; RMS gating would starve it and clip onsets.
- **Endpoint rules**: rule 1 (nothing said) 2.4 s, rule 2 (after speech) `StreamingEndpointMs` (800 default,
  300–3000), rule 3 (max utterance) 20 s.
- **Text rendering**: sherpa removes spaces between "CJK" characters and counts Hangul among them. Its tokens keep the
  SentencePiece marker as a leading space (`[ 지하철][에서][ 다리][를]…`, plus orphan `[ ]` tokens), so
  `SherpaStreamingStt.Render` rebuilds words from the tokens when they spell exactly sherpa's text.
- **Residency**: the recognizer is cached per process per (model, options), like the voice engines.
- **Natives**: sherpa-onnx 1.13.8 ships `onnxruntime.dll` 1.28.2, which now wins the output folder over
  Microsoft.ML.OnnxRuntime 1.23.2's. The managed 1.23.2 API runs on it (re-verified: Supertonic, KittenTTS 2 decoder,
  offline diarization). Rule: never pin ORT's native *below* sherpa's — sherpa asks for its own API version and would
  fail to start.

## Measurements (this machine, CPU, Korean test clips shipped in the Zipformer archive)

`AgentZeroWearable.exe --hear-stream <clip> --stream-model <id> --lang ko [--realtime]`

| | Nemotron 3.5 · 560 ms | Zipformer ko |
|---|---|---|
| Load (cold / cached) | 3.6 s / 2.8 s | 4.0 s / 2.9 s |
| RTF, unpaced (2 threads) | 0.37–0.48 | 0.17–0.22 |
| First partial, clip 1 (sample-accurate, incl. the clip's leading silence) | 1.30 s of audio | 1.10 s of audio |
| Clip 2 (6.6 s), paced in real time: full sentence on screen | +0.4 s after clip end | +0.2 s |
| …final emitted (endpoint 0.8 s) | +0.9 s after clip end | +1.2 s |
| Clip 0 "그는 괜찮은 척하려고 애쓰는 것 같았다." | exact (no final period) | "걔는 … 척할려구 … 거" (colloquial spelling) |
| Clip 2 "…다른 집 아이와…" | exact | "다른 집안과" (error) |

Both kept up in real time with a constant ~0.4 s lag (wall − audio). The batch path for the same clip waits the full
hangover (1.2–2 s) and then a Whisper pass.

## Where it plugs in (Phase 1 — shipped in the worktree)

```
mic (NAudio, 50 ms) ─Tell(MicFrame)→ VoiceStreamActor
   StartListening → StreamingSttFactory() != null ?
      yes: Source.Queue(200, DropHead) → KillSwitch → StreamingSttFlow (blocking-IO dispatcher)
           → Sink.ActorRefWithAck(Self) → OnPartial (txtInput preview) / OnTranscript (send)
      no : the batch graph (VoiceSegmenterFlow → STT pool), unchanged
```

Hosts:

- **Avalonia (official)** — 🎙 in the AgentBot composer: `AgentBotViewModel.Voice.cs` asks Stage for the voice
  actor once (`CreateVoiceStream` with `PortableVoiceRuntime.CreateStreamingStt` as the streaming factory), and each
  mic-on is a `VoiceMicSession` (ZeroCommon) over `AudioCapture.Platform`. Settings → Voice adds the picker,
  Download, endpoint and **🎙 Listen live** (mic → session directly, words in *Heard*). Windows only until macOS has
  an `IAudioCapturePlatform`; the 🎙 hides itself there.
- **WPF (reference)** — its NAudio `VoiceCaptureService` forwards frames to the same actor.
- Verified headless through the real model: `StreamingSttLiveTests.Mic_session_through_the_voice_actor_sends_one_settled_sentence`
  plays a Korean clip as a microphone at real-time pace → partials → one final (~4.5 s after start for a 2.4 s clip
  plus 0.8 s endpoint and the model's load).

- The factory is asked on every `StartListening`, so a provider switch applies at the next mic-on.
- An uninstalled model → factory returns null → batch path with Whisper Local (`VoiceRuntimeFactory.BuildStt`).
- `StreamingSttBatchAdapter` makes the same model serve every `ISpeechToText` caller (Voice Test, voice notes).
- Mic-off completes the queue first; the stage flushes the decoder (`Finish`) on upstream-finish.

## Phase 2 — reacting while the person is still speaking (not built yet)

The agent loop must start on settled words only (tools have side effects). What can start earlier, in order of risk:

1. **Command intents on partials** — `VoiceCommandInterceptor.Classify` on each partial: "그만" → `BargeIn`
   immediately, not after the endpoint. Zero side-effect risk; biggest felt win while TTS is talking.
2. **Semantic endpoint** — when a partial ends a sentence (Nemotron's `.?!`, or Korean sentence-final endings
   -다/-요/-까/-죠) shorten the endpoint to ~300 ms for that utterance; keep 800 ms for trailing clauses. Implement
   as an `EndpointPolicy` stage after `StreamingSttFlow` that can force `Reset` (needs a session method
   `ForceEndpoint()`).
3. **LLM prefill / warm-up on a stable partial** — partial unchanged for ~300 ms: warm the local LLM (Deferred
   load), build the prompt frame, start KV prefill of the system + history (the request tail is appended at the
   final). External providers: open the HTTP/2 connection. Message: a new `VoicePartialStable(text)` to
   `AgentBotActor`, which holds it and discards on the next partial.
4. **Speculative no-tool draft** — run one tool-less model call on the stable partial; at the final, commit it if
   the final extends the partial (prefix match after normalisation), else cancel. Only for chat mode / a router
   verdict of "answer directly"; never in AI mode with tools.
5. **Backchannel** — a short TTS acknowledgement ("네,") at the final while the loop thinks; cancelled by barge-in.

Akka.Streams shape for 1–3: `StreamingSttFlow → Broadcast` → (a) the existing sink, (b) `Select(Classify)` →
command sink, (c) `GroupedWithin`/stability stage → `VoicePartialStable` to the bot.

## Open items

- Avalonia: voice output for AgentBot answers (TTS + barge-in) — needs a portable `IAudioPlaybackQueue`; macOS mic.
- Wearable: the watch is push-to-talk with ADPCM blocks (`ChatActor.MicFrame`); feeding the decoded blocks into a
  session would show partials on the watch — needs a protocol message for partial text.
- The 160 ms Nemotron variant is in the catalog but not yet measured here.
- `UseStreamPipeline` stays off by default; choosing the Streaming provider implies the stream path.
