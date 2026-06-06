# Speaker diarization pipeline — capture → STT + diarize → merge → render

**Owner**: voice-curator
**Lifecycle**: convention — binding for any change to how Voice test or voice-note plugin renders speaker labels
**Last updated**: 2026-06-06

## End-to-end shape

```
   ┌───────────────────────────────────┐
   │  Capture source (16 kHz mono PCM) │
   │   • Microphone (NAudio WaveIn)    │  ← existing Voice pipeline
   │   • System Output (WASAPI)        │  ← reuse Music tab LoopbackCaptureService (M0024)
   │   • File drag (Phase 3 option)    │  ← future
   └─────────────┬─────────────────────┘
                 │ byte[] pcm16
        ┌────────┴─────────┐
        ▼                  ▼
   ┌─────────┐       ┌──────────────────┐
   │ Whisper │       │ SherpaDiarizer   │
   │  (STT)  │       │ (segmentation +  │
   │         │       │  embedding +     │
   │ text    │       │  clustering)     │
   │ spans   │       │                  │
   │ Start   │       │ speaker spans    │
   │ End ms  │       │ Start/End sec    │
   │ text    │       │ + SpeakerId      │
   └────┬────┘       └────────┬─────────┘
        │                     │
        └──────────┬──────────┘
                   │ Merge by interval overlap
                   ▼
       ┌─────────────────────────┐
       │ Annotated transcript    │
       │  • Speaker A: "안녕..." │
       │  • Speaker B: "네 ..."  │
       │  • Speaker A: "그럼..." │
       └────────────┬────────────┘
                    │
        ┌───────────┴────────────┐
        ▼                        ▼
   ┌────────────┐         ┌─────────────────┐
   │ Voice test │         │ voice-note      │
   │ panel      │         │ plugin (web)    │
   │ (in-app)   │         │ note.onTransc.  │
   └────────────┘         └─────────────────┘
```

## Merge logic — Whisper text spans + Sherpa speaker spans

Whisper emits transcript segments with `Start`, `End`, `Text`. Sherpa emits
diarization segments with `Start`, `End`, `SpeakerId`. Combining is a
**weighted interval-overlap assignment**:

```text
for each whisper_segment ws in transcript:
    best_speaker  = -1
    best_overlap  = 0
    for each sherpa_segment ss in diarization:
        ov = overlap_sec(ws, ss)
        if ov > best_overlap:
            best_overlap = ov
            best_speaker = ss.Speaker
    ws.Speaker = best_speaker  (or -1 if no overlap)
```

### Edge cases

| Case | Behavior |
|---|---|
| Whisper segment spans 2 Sherpa speakers | Pick the speaker with majority overlap; future: split the Whisper segment at the speaker boundary |
| Whisper segment with zero Sherpa overlap | `Speaker = -1` → render as "?" (no speaker label) |
| Sherpa boundary falls mid-word in Whisper | Accept the boundary drift — sub-word splitting requires character-level timestamps Whisper doesn't expose by default |
| Single-speaker clip | All Whisper segments get `Speaker = 0` → render as "Speaker A" — no UI penalty for monospeaker |
| Overlapping speech (both speakers talking at once) | Whisper transcribes the louder voice; Sherpa segments both; merge picks the speaker with more overlap in that window |
| Sub-millisecond drift between clocks | Use `double` seconds throughout; Whisper rounds to 0.01 s, Sherpa to ~0.02 s — drift is bounded |

### Output schema (used by voice-note bridge + Voice test panel)

```typescript
interface AnnotatedSegment {
  startSec: number;        // float
  endSec: number;          // float
  text: string;            // Whisper output
  speakerId: number;       // 0-based; -1 = unknown
  speakerLabel: string;    // "Speaker A" / "Speaker B" / "?"
}
```

## Voice test panel rendering

When `DiarizationSettings.Provider != Off` AND model is ready, the Voice
test transcript panel renders each Whisper line prefixed with its
`speakerLabel`:

```
[Speaker A] 안녕하세요, 회의 시작하겠습니다.
[Speaker B] 네, 안녕하세요. 자료 공유 부탁드립니다.
[Speaker A] 화면 공유 켰습니다. 잘 보이시나요?
[?]         (잘 안 들림)
```

When provider is `Off`, render the existing format unchanged (no prefix,
backward compatible).

## voice-note web plugin rendering

The bridge contract (`WebDevHost.cs`) gains an optional `speakerId` field
on the `note.onTranscript` payload:

```javascript
window.zero.note.onTranscript((data) => {
  // data.text         — string (existing)
  // data.startMs      — number (existing)
  // data.endMs        — number (existing)
  // data.speakerId    — number | undefined (new, M0024)
  // data.speakerLabel — string | undefined (new, M0024)
});
```

`voice-note.js` `renderRaw()` adds the speaker prefix as a colored chip
when present. CSS palette per speaker — first 4 colors match the
spectrum bar gradient already used by Music tab so the visual language
stays consistent across the app:

| SpeakerId | Hex | Variable |
|---|---|---|
| 0 (Speaker A) | `#00E5FF` (cyan) | `--vn-spk-a` |
| 1 (Speaker B) | `#C586C0` (magenta) | `--vn-spk-b` |
| 2 (Speaker C) | `#7CFDB0` (mint) | `--vn-spk-c` |
| 3 (Speaker D) | `#FFB800` (amber) | `--vn-spk-d` |
| 4+ | generated HSL with golden-angle stride | dynamic |
| -1 (unknown) | `--text-muted` (grey) | — |

Backward compatibility: when the bridge sends a payload without
`speakerId`, the plugin renders the existing way (no chip, monochrome
text). No breaking change for plugins that don't know about diarization.

## Test fixture for merge correctness

A small canned test clip (~15 s, 2 speakers alternating greetings) lives
in the test project as a baseline. The merge function is expected to
produce monotonic speaker labels with sub-200 ms boundary drift on this
fixture. Regression = fail in `voice-curator` evaluation rubric axis
"Merge correctness".

Future test improvements:
- Add a 3-speaker overlap clip
- Add a single-speaker monologue (sanity check: no spurious speaker splits)
- Add a Korean-only clip (verify the 3D-Speaker zh-cn embedding works
  cross-language as documented)

## References

- Sherpa-ONNX integration contract:
  `harness/knowledge/voice-curator/sherpa-onnx-integration.md`
- Existing Voice pipeline (STT) — `Project/ZeroCommon/Voice/`
- WebDev bridge — `Project/AgentZeroWpf/Services/Browser/WebDevHost.cs`
- voice-note plugin — `Project/Plugins/voice-note/`
