# Sherpa-ONNX speaker diarization — integration contract

**Owner**: voice-curator
**Lifecycle**: convention — binding for any change to `SherpaSpeakerDiarizer` or `SherpaDiarizationModelDownloader`
**Last updated**: 2026-06-06

## Why Sherpa-ONNX and not VibeVoice

Mission M0024 originally proposed `microsoft/VibeVoice` (the model the
operator linked). Research findings (logged with the mission completion
record):

| Concern | VibeVoice-ASR | Sherpa-ONNX (chosen) |
|---|---|---|
| Speaker diarization | ✅ DER 4.28% | ✅ practical-level |
| Model size | 7B params (~4-5 GB Q4) | ~50 MB (seg + embed) |
| ONNX export | ❌ no official, no community for the 7B ASR | ✅ pure ONNX, k2-fsa release |
| C# API | ❌ Python only | ✅ `org.k2fsa.sherpa.onnx` NuGet |
| "Embedded like Music" | ❌ unfeasible | ✅ lighter than AST 86M |

The operator's brief explicitly prioritised "embedded, no pip, like the
recently added Music model" — that constraint ruled out VibeVoice. The
chosen alternative achieves the **same capability** (speaker labels in
voice notes + Voice test) at 1/100th the model size.

If VibeVoice ever publishes an official ONNX export (HF discussion #19
currently open), revisit — it would fit `ISpeakerDiarizer` as a drop-in
implementation without UI changes.

## Model pair — what we ship

| Role | File | Source | Size | License |
|---|---|---|---|---|
| Segmentation | `segmentation.onnx` (from `sherpa-onnx-pyannote-segmentation-3-0.tar.bz2`) | k2-fsa releases | ~6 MB | MIT |
| Embedding | `3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx` | k2-fsa releases | ~40 MB | Apache 2.0 |

### Why this exact pair

- **pyannote-segmentation-3-0**: Sherpa-ONNX maintainers ship the v3.0
  weights as ONNX. Upstream pyannote v3.1+ removed ONNX support
  (PyTorch-only), so v3.0 is the practical embedded ceiling today.
  Frame-level speaker-change detection accuracy is essentially
  unchanged between v3.0 and v3.1 for the diarization use case.
- **3D-Speaker ERes2Net base zh-cn**: despite the "zh-cn" suffix this
  embedding model performs well across languages including Korean
  (CN-Celeb / VoxCeleb cross-language transfer is empirically robust
  for embedding-based clustering). Korean-tuned alternatives exist
  but are larger and don't materially improve cluster purity in
  bilingual KO+EN scenarios that are typical for AgentZero users.
- **Pair compatibility**: Sherpa documentation explicitly lists this
  segmentation+embedding combination as a tested pairing.

### Direct URLs (stable)

```
https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-segmentation-models/sherpa-onnx-pyannote-segmentation-3-0.tar.bz2
https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx
```

> ⚠ Note the typo `speaker-recongition-models` in the release tag URL —
> that's the upstream typo, not ours. Don't "fix" it on our side or the
> download breaks.

## Cache layout

```
%LOCALAPPDATA%\AgentZeroLite\models\sherpa-diarization\
├── segmentation.onnx                  ~ 6 MB
└── embedding.onnx                     ~ 40 MB
```

Convention paths resolved via
`DiarizationSettingsStore.ResolveSegmentationPath` /
`ResolveEmbeddingPath`. Missing files surface as
"✗ Segmentation model missing: …" in `EnsureReadyAsync` rather than a
runtime crash on the first `Process` call.

## C# API surface (Sherpa-ONNX 1.10.x)

```csharp
using SherpaOnnx;

var config = new OfflineSpeakerDiarizationConfig();
config.Segmentation.Pyannote.Model = segPath;   // ONNX file
config.Embedding.Model               = embPath;  // ONNX file
config.Clustering.NumClusters        = -1;       // -1 = auto via threshold
// Optional: config.Clustering.Threshold = 0.5f
// Optional: config.Segmentation.NumThreads = 0
// Optional: config.Embedding.NumThreads    = 0

var sd = new OfflineSpeakerDiarization(config);
// sd.SampleRate == 16000

float[] samples = ... ;          // 16 kHz mono, [-1, +1]
var segments = sd.Process(samples);
foreach (var s in segments)
{
    // s.Start (float, seconds)
    // s.End   (float, seconds)
    // s.Speaker (int, 0-indexed)
}
```

### Constraints

- **Sample rate 16 kHz mono only** — must resample any non-16k input
  before `Process`. AgentZero's existing
  `AgentZeroWpf.Services.Music.LoopbackCaptureService` already does this
  (`BufferedWaveProvider → StereoToMono → WdlResamplingSampleProvider(16k)
  → SampleToWaveProvider16`), reuse it for the Voice tab's loopback PCM
  source path.
- **`Process` is synchronous** — wrap in `Task.Run` to keep the WPF
  dispatcher responsive. Already done in `SherpaSpeakerDiarizer`.
- **`OfflineSpeakerDiarization`** name = "offline" because it runs on a
  complete buffer, not streaming. Live diarization (streaming) is a
  Phase 5 concern, would need `OnlineSpeakerDiarization` (not yet shipped
  by upstream as of 1.10.46).

## Threading

- `EnsureReadyAsync` builds the session on a thread-pool thread under a
  lock so multiple concurrent first-calls don't race the constructor.
- `DiarizeAsync` runs `Process` on a thread-pool thread; the returned
  `DiarizationResult` is freshly allocated and safe to hand to the WPF
  dispatcher.

## NuGet versioning policy

- Package: `org.k2fsa.sherpa.onnx` (lowercase, dot-separated — k2-fsa
  convention, not the C#-typical `K2Fsa.Sherpa.Onnx`)
- Pinned: `1.10.46` (latest stable as of M0024 ship)
- Bump policy:
  - **Patch** (1.10.x → 1.10.y) — safe to take eagerly, k2-fsa maintains
    backward compatibility within a minor
  - **Minor** (1.10 → 1.11) — re-test the diarization pipeline end-to-end
    against the test fixture; check the changelog for
    `OfflineSpeakerDiarizationConfig` property renames
  - **Major** — voice-curator review mandatory

## Adding a new diarizer — checklist

When the next diarization model lands (pyannote subprocess, NeMo
Sortformer, WhisperX), voice-curator walks the requester through:

1. **New `ISpeakerDiarizer` implementation** in
   `Project/ZeroCommon/Voice/Diarization/`. Same return type
   (`DiarizationResult`), same input contract (16 kHz mono PCM16).
2. **Preprocessing module** if the model wants different feature
   extraction (e.g. mel features for some pyannote variants).
3. **Provider name constant** in `DiarizationProviderNames`.
4. **Settings field** in `DiarizationSettings` if model-specific.
5. **Settings tab UI** — Provider ComboBox gains a new
   `<ComboBoxItem Tag="...">` (UI is already factored to accept
   multiple options).
6. **Download path** — extend `SherpaDiarizationModelDownloader` or add
   a sibling downloader, share the `ModelDownloadDialog` contract.
7. **Knowledge entry** —
   `harness/knowledge/voice-curator/<model>-integration.md` alongside this.

## References

- Sherpa-ONNX docs (speaker diarization):
  <https://k2-fsa.github.io/sherpa/onnx/speaker-diarization/index.html>
- Sherpa-ONNX C# API: <https://k2-fsa.github.io/sherpa/onnx/csharp-api/index.html>
- pyannote-segmentation release tag:
  <https://github.com/k2-fsa/sherpa-onnx/releases/tag/speaker-segmentation-models>
- 3D-Speaker embedding release tag:
  <https://github.com/k2-fsa/sherpa-onnx/releases/tag/speaker-recongition-models>
- VibeVoice (rejected, kept for posterity):
  <https://github.com/microsoft/VibeVoice>
