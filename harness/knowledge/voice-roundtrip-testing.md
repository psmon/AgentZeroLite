# Voice TTS↔STT Round-Trip Testing — Methodology

> Owner: **`test-sentinel`** (primary) — owns the test-execution canon and
> the `AgentTest`-vs-`ZeroCommon.Tests` split this work targets.
> Cross-reference: **`code-coach`** (secondary) — fuzzy-comparison
> normalisation patterns and the "model auto-corrects, test should not
> alarm" judgment apply to other LLM-output testing too.

This is a record of the testing pattern that landed in
`Project/AgentTest/Voice/TtsSttRoundTripTests.cs` on 2026-04-29
(commit `b1a0ec8`) — pure-model file-based round-trip tests for the
voice subsystem. Captures the design choices and the non-obvious
findings so future test work in this area starts from the same baseline.

---

## Purpose

We can't measure "is the voice subsystem getting better or worse" if
the only signal is the user clicking buttons in TestToolsWindow and
eyeballing transcripts. Need a deterministic, repeatable harness that:

1. Runs without a microphone, speaker, or human in the loop.
2. Goes through the actual TTS provider + the actual STT provider
   (not mocks) — measuring the model pair's true round-trip quality.
3. Reports per-stage timing, audio levels, exact and fuzzy
   comparisons every run, regardless of pass/fail.
4. Saves the produced audio (both as TTS emitted and as STT received)
   so a human can listen when text comparison disagrees with
   expectation.

The pattern is **pure-model, file-based, whole-blob, no Akka, no
streams** — a clean reference. The streaming / Akka path's quality
becomes measurable as a delta against this baseline.

---

## Topology

```
input string
   ↓
WindowsTts.SynthesizeAsync(input, voice=default)   — SAPI, blocking
   ↓ wav bytes (16-bit PCM, sample rate per voice)
File.WriteAllBytes(*-tts.wav)                      — evidence artifact
   ↓
WavToPcm.To16kMono(wav)                            — NAudio resample
   ↓ pcm bytes (16 kHz / 16-bit / mono)
File.WriteAllBytes(*-stt-input-16k.wav)            — evidence artifact
   ↓
WhisperLocalStt.TranscribeAsync(pcm, language)     — Whisper.net medium, CPU
   ↓ transcript string
Normalize(input) == Normalize(output)              — fuzzy compare
```

No actors, no streams, no UI thread, no mic, no speaker. Each stage's
elapsed time is measured with `Stopwatch` and printed in the test
output.

---

## Lessons learned during the 2026-04-29 build

### 1) Naive string equality is too strict; raw similarity is too loose

Whisper outputs proper-case + punctuation regardless of input shape:

| input          | Whisper output  | Levenshtein dist | actual content drift? |
|---|---|---|---|
| `hello`        | `Hello.`        | 2                | no — tokeniser cosmetics |
| `how are you?` | `How are you?`  | 1                | no — capital H only |
| `안녕하세요`    | `안녕하세요`    | 0                | no |

Folding case + punctuation + whitespace before comparison is the
right normalisation level. After folding, real hallucinations
(substitutions, insertions, truncations) become visible while tokeniser
artefacts don't pollute the signal.

```csharp
private static string Normalize(string s)
{
    if (string.IsNullOrEmpty(s)) return string.Empty;
    var sb = new StringBuilder(s.Length);
    foreach (var c in s)
    {
        if (char.IsWhiteSpace(c)) continue;
        if (char.IsPunctuation(c)) continue;   // covers .,?!;:'"() and more
        sb.Append(char.ToLowerInvariant(c));    // no-op for Korean
    }
    return sb.ToString();
}
```

`char.IsPunctuation` covers a wider set than a hard-coded list (smart
quotes, dashes, full-width punctuation that Whisper emits for Korean).
ToLowerInvariant is a no-op for Korean since Hangul has no case, so
applying it unconditionally costs nothing.

### 2) Whisper auto-corrects semantic typos — the test must report this honestly

The fixture `"내일의 날씨는 말고 모래의날씨는 흐리고…"` (user's own input)
contains a homophone typo: `모래` means "sand", `모레` means "day after
tomorrow". In context, `모레` is unambiguously correct, and Whisper
recognised it as `모레`. Edit distance 1, 96.8% similarity, **content
drift = 1 character that the model corrected**.

This is the exact kind of finding the harness should surface — not
hide via a higher similarity threshold and not "fix" by editing the
input. The test is a measurement instrument; let it measure.

Implication for future fixture choices:
- Use the **user's actual production fixture** when available, even if
  it has a typo. The point is to measure what the deployed pipeline
  does on real input.
- Treat similarity-but-not-exact as a signal worth investigating, not
  noise to suppress. Console output describes both `normalised input`
  and `normalised output` so the operator can read the diff at a
  glance.

### 3) Dual output channel: ITestOutputHelper + Console.WriteLine

xUnit's `ITestOutputHelper` is the canonical per-test capture, shown in
the test explorer / pass-fail listing. But `dotnet test` with
`--logger "console;verbosity=detailed"` also captures `Console.Out`.
The two paths display in different views and have different ergonomics
when the test fails.

Write to both, always:

```csharp
private void Log(string line)
{
    try { _output.WriteLine(line); } catch { /* helper may be disposed */ }
    Console.WriteLine(line);
}
```

The `try/catch` matters — when an `Assert.Fail` runs, the test helper
may have been finalised by the time later cleanup tries to write,
and we don't want a secondary failure to mask the original.

### 4) Always save the audio as evidence

When the comparison fails, the next question is "does the audio sound
right?" Saving both stages of the WAV pipeline lets a human answer
that without re-running the test:

```
%TEMP%\agentzero-tts-stt-tests\
  20260429-180920-162-ko-long-tts.wav             ← what SAPI produced
  20260429-180920-162-ko-long-stt-input-16k.wav   ← what Whisper saw
```

Listening in order:
- `*-tts.wav` sounds normal but the test failed → STT model issue.
- `*-stt-input-16k.wav` sounds wrong → resample / `WavToPcm` regression.
- Both sound fine but transcript is junk → genuine model hallucination.

Each branch points at a different fix. Without the artifacts you'd
have to re-instrument and re-run.

### 5) Whisper medium CPU performance baseline (record now, regress later)

Measured on the maintainer's dev machine 2026-04-29:

| input length | TTS ms | decode ms | STT ms | xRT  | total ms |
|---|---|---|---|---|---|
| 5 chars (Korean) | 30   | 1   | 3000 | 3–5x | ~3s |
| 11 chars (Korean) | 33  | 1   | 8915 | 4.09x | 8.9s |
| 42 chars (Korean) | 66  | 5   | 9948 | 1.68x | 10.0s |
| 5 chars (English) | 25  | 1   | 6000 | 3–5x | ~6s |
| 12 chars (English) | 43 | 11  | 9478 | 6.30x | 9.5s |

Notes:
- **xRT improves on longer audio** because Whisper's per-call overhead
  dominates short clips. The 1.68× on a 5.93s utterance is realistic
  steady-state CPU performance.
- TTS is two orders of magnitude faster than STT — synth is rarely
  the bottleneck.
- First test pays the model load (~9s prep). Subsequent tests in the
  same run hit cache (`prep: 0 ms`).

Use these numbers as the regression baseline when adopting:
- A different STT (cloud Whisper, Webnori Gemma, on-device Gemma)
- A different model size (tiny / small / large)
- GPU acceleration (Vulkan / CUDA)

If a swap shows xRT going up by >1.5× with no quality gain, that's
a regression worth investigating before merging.

### 6) Make the resample helper public so the test can see exactly what STT sees

`WavToPcm.To16kMono` was `internal` — fine for production callers, but
`AgentTest` couldn't reach it without InternalsVisibleTo plumbing. The
helper has zero encapsulation secrets worth defending; flipping to
`public` was the right trade. Moral: when a static helper is the
authoritative implementation of a transformation, make it accessible
to its tests directly. InternalsVisibleTo is reserved for cases
where the test needs to peer at *production state*, not just call
production functions.

---

## Operational rules for future expansion

1. **Always use the same fixtures the production UI uses.** The
   quick-phrase buttons in `TestToolsWindow.xaml` are the canonical
   test inputs. Mirror them verbatim in
   `TtsSttRoundTripTests.cs` so QA and dev test the same thing.
2. **Console output is the report.** Don't try to fold the test into a
   green/red gate alone — the per-stage timings, audio levels, and
   side-by-side comparison are the deliverable. Strip them at your peril.
3. **Save WAV evidence on every run.** Disk is cheap; round-trip retests
   are not. The two-WAV pattern (raw TTS + STT-input) makes failures
   diagnosable from artifacts without re-running.
4. **Add cases when adding fixtures.** New TTS providers, new STT
   providers, new languages → new `[Fact]` rows. Don't parameterise
   into `[Theory]` until the case count is high enough that the noise-
   to-signal of `Theory` data tables outweighs the simplicity of named
   `[Fact]` methods.
5. **Don't suppress drift via a fuzzy threshold.** Surface the
   1-char-different case as a fail, with full diff in the console.
   It's a more useful signal than a "passed at 95% threshold" green
   tick that hides which character the model substituted.

---

## Where this knowledge applies

- `harness/agents/test-sentinel.md` — when reviewing voice-related
  tests, this file's "Operational rules" section is binding (especially
  rule 1, fixture parity).
- `harness/agents/code-coach.md` — when reviewing diffs that change
  TTS, STT, or any LLM-output comparison logic, the normalisation
  pattern (case+punct+whitespace fold, lowercase) is the recommended
  default. Naive `==` is too strict; loose contains-substring is too
  permissive.
- Future audio-pipeline regression suites (Akka.Streams variant,
  cloud STT swap, GPU acceleration) — start from this file's
  performance baseline and report deltas, not absolutes.
