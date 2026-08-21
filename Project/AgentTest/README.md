# AgentTest

WPF-dependent xUnit suite (actors, ConPTY session, approval parser, voice).
References both `AgentZeroWpf` and `ZeroCommon`, so it needs a desktop
session and the WPF host to build/run.

## Which suite gates a merge

**`ZeroCommon.Tests` is the authoritative merge gate** — it is WPF/Win32-free,
runs headless, and is the suite that is trustworthy unattended. A green
`ZeroCommon.Tests` run is the verification cited on PRs.

`AgentTest` is a **secondary, desktop-session suite**. A green `AgentTest`
run means the *non-native* subset passed; it is not a full-suite guarantee.

## Whisper / whisper.cpp native tests (GitHub #14)

The voice tests in `Voice/TtsSttRoundTripTests.cs` and
`Voice/WhisperCpuVsGpuBenchmarkTests.cs` exercise the **whisper.cpp native
runtime** through Whisper.net. On some environments (this development machine
included) the native runtime crashes the test host with an unmanaged access
violation (`0xC0000005` in `WhisperProcessor.GetWhisperState`), which xUnit
cannot catch and which silently truncates the rest of the suite — the exact
"green AgentTest proves very little" failure mode from #14.

To keep the suite stable and honest, those tests are gated by
`[WhisperNativeFact]` (`Voice/WhisperNativeFact.cs`). A gated test **runs
only when both** hold:

1. A whisper model file (`ggml-*.bin`) is present under
   `%USERPROFILE%\.ollama\models\agentzero\whisper\`, **and**
2. `AGENTZERO_VOICE_NATIVE_TESTS=1` (or `true`) is set — an explicit opt-in
   on a machine where the native runtime is known-good.

Otherwise the test is **skipped with a clear reason** (never a silent pass,
never a host crash).

To run them locally:

```powershell
# PowerShell
$env:AGENTZERO_VOICE_NATIVE_TESTS = "1"
dotnet test Project/AgentTest/AgentTest.csproj -c Debug --filter "FullyQualifiedName~Voice"
```

```bash
# bash / cmd
AGENTZERO_VOICE_NATIVE_TESTS=1 dotnet test Project/AgentTest/AgentTest.csproj -c Debug --filter "FullyQualifiedName~Voice"
```

> Note: model presence alone is **not** sufficient on this dev box — the
> model is present and the native runtime still crashes the host. The opt-in
> is deliberate so a crash there is an explicit operator choice, not a silent
> CI break. The native crash itself (whisper.cpp init) is a separate issue
> and is not fixed by this gate; the gate only stops it from masking the
> rest of the suite.

## Running the suite

```bash
dotnet test Project/AgentTest/AgentTest.csproj -c Debug
```

Expected (native tests gated): a stable, identical test count across runs —
e.g. `전체=151, 통과=144, 건너뜀=7, 실패=0` — with no host crash.
