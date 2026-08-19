# Native inference lifetime & serialization — voice-note pipeline

**Owner**: voice-curator
**Lifecycle**: convention — binding for any change to `SherpaSpeakerDiarizer`, `WebDevHost` note pipeline, or any new native-inference surface in the note path
**Last updated**: 2026-08-19

Two contracts that exist **only because** of the 2026-07-15 crash sequence
(loopback concurrency crash → P0 gate patch → chunk-scoped re-implementation).
The v0.16.0 audio-regression review (2026-08-09) flagged this knowledge as
missing — this file closes that gap.

## Contract 1 — chunk-scoped native lifetime (per-utterance dispose)

`SherpaSpeakerDiarizer.DiarizeAsync` (Project/ZeroCommon/Voice/Diarization/SherpaSpeakerDiarizer.cs):

- **No persistent native instance.** Every call does
  `using var sd = new OfflineSpeakerDiarization(BuildConfig(seg, emb));`
  → `Process` → dispose. The ONNX Runtime arena is returned to the OS at
  the end of each utterance, exactly like `WhisperLocalStt`'s per-call
  `await using processor`.
- `EnsureReadyAsync` performs model-file validation + a **one-shot warm
  build/dispose** (early error exposure, disk cache prime). Only the resolved
  model paths are cached (`_segPath`/`_embPath`, `_validated` flag).
- `BuildConfig()` is the single source of the config for both the warm and
  chunk paths — never fork a second config, or the model pair
  (pyannote-seg-3.0 + 3D-Speaker, 16 kHz mono contract) can drift.
- `DisposeAsync` is intentionally near-no-op: with no persistent handle there
  is nothing to leak. If you re-introduce a persistent instance, you must
  also re-introduce real disposal.

### Why (the failure this prevents)

A persistent `OfflineSpeakerDiarization` reuses one ONNX arena across
utterances; the arena grows to peak and is **never returned to the OS**.
On an integrated GPU (AMD Radeon 8060S — VRAM is shared system RAM) that
accumulation plus Whisper Vulkan allocations hard-faulted the driver after
~5 minutes, with `inferMs` climbing 6650 → 10099 as the leading indicator.
The STT path survived the same sessions because it disposes per call.

### Cost trade-off & the escape hatch

Chunk-scoping re-inits ~46 MB of ONNX per utterance. The log line to watch:

```
[Diar] chunk-scoped | buildMs=… inferMs=… samples=… segs=… speakers=…
```

If `buildMs` becomes dominant (> ~1–2 s sustained), the sanctioned fix is a
**recycle-every-N cache** localized to `DiarizeAsync` alone — do NOT revert
to a fully persistent instance.

## Contract 2 — `_noteInferGate` serialization (single choke point)

`WebDevHost` (Project/AgentZeroWpf/Services/Browser/WebDevHost.cs):

- `SemaphoreSlim _noteInferGate(1,1)` is the **only** serialization point
  for all native inference in the note pipeline. Two native inferences must
  never overlap — this is what killed the shared whisper.cpp (Vulkan) /
  Sherpa state before the patch.
- `ProcessFinalChunkAsync` (mic utterance AND loopback chunk — both sources
  funnel here, no per-source fork): **blocking** `WaitAsync(ctToken)` on
  entry, `Release` in `finally`. A cancelled wait (STOP) returns quietly.
- Partial preview (`OnNotePartialTick`): **non-blocking** `WaitAsync(0)`
  try-acquire. If anything else holds the gate the partial is skipped, not
  queued — partials are best-effort previews, and queuing behind a chunk
  STT would build a native-inference backlog.
- Re-entrancy guards are orthogonal: `_noteChunkBusy` / `_notePartialBusy`
  (Interlocked) prevent timer self-reentry; the gate prevents cross-surface
  overlap. Don't conflate the two.

### Rule for new surfaces

Any new native-inference call in the note pipeline (new model, new
transcribe path, on-device vision frame, …) MUST go through
`_noteInferGate` with the same blocking/try-acquire discipline. A new
semaphore for a new surface is the exact anti-pattern that caused the
original crash.

## Verification debt (open as of 2026-08-19)

- [ ] Real-device 5-min+ mic session: confirm `[Diar] chunk-scoped`
      `buildMs` within tolerance + no crash + memory stable. (A- rating
      condition from 2026-07-15 17:30 log; still unverified.)
- [ ] If `buildMs` over budget: recycle-every-N in `DiarizeAsync` (see above).
- [ ] Second-line defense if crashes recur: Whisper `SttUseGpu=false` (drop
      iGPU Vulkan contention) or STT small model.

## References

- Crash RCA: `harness/logs/voice-curator/2026-07-15-16-08-voice-note-loopback-concurrency-crash.md`
- P0 gate patch: `harness/logs/voice-curator/2026-07-15-16-20-voice-note-infer-gate-p0-patch.md`
- P1 dispose patch: `harness/logs/voice-curator/2026-07-15-16-40-diarizer-native-dispose-p1-patch.md`
- Chunk-scoped re-implementation: `harness/logs/voice-curator/2026-07-15-17-30-diarizer-chunk-scoped-memory-model.md`
- Engine verdict: `harness/logs/voice-curator/2026-08-09-09-24-v0160-audio-regression-review.md`
- Model pair / integration: `harness/knowledge/voice-curator/sherpa-onnx-integration.md`
- Pipeline shape / merge logic: `harness/knowledge/voice-curator/speaker-diarization-pipeline.md`
