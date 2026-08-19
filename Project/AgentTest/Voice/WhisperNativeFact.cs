// ───────────────────────────────────────────────────────────
// WhisperNativeFact — conditional gate for Whisper.net (whisper.cpp)
// native-runtime tests.
//
// GitHub #14: the AgentTest suite was reporting success while a native
// crash in whisper.cpp (`0xC0000005` in WhisperProcessor.GetWhisperState)
// killed the testhost mid-run, silently truncating the suite ("green
// AgentTest proves very little"). The crash is in the native runtime
// itself (model load / first inference), NOT in our managed code, and it
// takes the whole testhost process down — xUnit cannot catch an unmanaged
// access violation.
//
// We therefore gate the model-dependent tests so they do NOT run by
// default in an unattended/CI context (where they would crash the host and
// hide the rest of the suite). Two independent conditions must hold for a
// gated test to actually execute:
//
//   1. The whisper model file is present (the issue's original "gate on the
//      artifact" intent).
//   2. An explicit opt-in: AGENTZERO_VOICE_NATIVE_TESTS=1 (or "true").
//      This is deliberate: on this development machine the model IS present
//      and the native runtime STILL crashes the host, so model presence
//      alone does not make the test safe to run. Opt-in means a machine
//      where the native runtime is known-good (CI with a validated runtime,
//      or a dev box that has confirmed a clean run) can still exercise the
//      tests — and a crash there is now an explicit operator choice rather
//      than a silent CI break.
//
// Usage:
//   [WhisperNativeFact]
//   public Task Korean_short_안녕하세요() => ...
//
// To run them locally:  $env:AGENTZERO_VOICE_NATIVE_TESTS = "1"
//   (PowerShell)   or   AGENTZERO_VOICE_NATIVE_TESTS=1 dotnet test ...
//   (bash/cmd)
// ───────────────────────────────────────────────────────────

using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AgentTest.Voice;

public sealed class WhisperNativeFact : FactAttribute
{
    public const string OptInEnvVar = "AGENTZERO_VOICE_NATIVE_TESTS";

    public WhisperNativeFact()
    {
        // Resolve the default model dir the same way WhisperLocalStt does
        // (%USERPROFILE%\.ollama\models\agentzero\whisper). We accept any
        // ggml-*.bin in that directory as "a model is present" so the gate
        // is robust to size choice (tiny/small/medium).
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ollama", "models", "agentzero", "whisper");

        bool modelPresent = false;
        try
        {
            modelPresent = Directory.Exists(dir) &&
                           Directory.EnumerateFiles(dir, "ggml-*.bin").Any();
        }
        catch
        {
            modelPresent = false;
        }

        if (!modelPresent)
        {
            Skip = $"Whisper model not present under {dir} — skipping model-dependent native test.";
            return;
        }

        var optIn = Environment.GetEnvironmentVariable(OptInEnvVar);
        bool optedIn = optIn is not null &&
                       (optIn == "1" || optIn.Equals("true", StringComparison.OrdinalIgnoreCase));
        if (!optedIn)
        {
            Skip = $"Whisper.net native runtime not validated in this environment — " +
                   $"skipping to avoid a native crash taking down the test host. " +
                   $"Set {OptInEnvVar}=1 on a machine with a known-good native runtime to run.";
        }
    }
}
