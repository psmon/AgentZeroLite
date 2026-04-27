---
date: 2026-04-28T10:15:00+09:00
agent: security-guard
type: review
mode: log-eval
trigger: "보안점검 실시"
scope: HEAD (last 4 voice-related commits) + standing scope items 4–7
issue: https://github.com/psmon/AgentZeroLite/issues/6
---

# Voice subsystem security review (post phase-2 commits)

## Trigger & framing

User invoked standalone — not gated by `release-build-pipeline`. Recent
work introduced a new attack surface (voice capture + STT/TTS providers +
HTTP egress to OpenAI/Webnori), so the pass focuses on those changes
plus the standing scope items most affected (native binary trust,
persistence, dependency drift, crash dump forensics).

Scope evaluated against HEAD (`1267da6`); 4 commits behind it
(`bf580b9`, `8e8a586`, `8fbcfcc`, plus the harness v1.1.5 doc which is
out of code scope).

## Coverage of the 7 standing scope items

| # | Scope item | Result | Notes |
|---|---|---|---|
| 1 | Injection surface (text → ConPTY) | **Pass** | F-7 — voice files import no `ITerminalSession`, `Process.Start`, or shell-exec path. Verified by grep. |
| 2 | Actor name sanitization | **Pass (N/A)** | Voice subsystem does not construct actor paths. |
| 3 | CLI ↔ GUI IPC | **Pass (N/A)** | No new `WM_COPYDATA` or MMF surface introduced by voice. |
| 4 | Native binary trust | **Pass with note** | F-3 — `System.Speech` preview package. Other natives (Whisper, NAudio) reputable + signed. |
| 5 | Persistence | **Pass with note** | F-2 — `voice-settings.json` plaintext keys. No EF migration needed; settings are JSON-side-car. |
| 6 | Dependency drift | **Pass with note** | F-3 covers preview; remaining packages are stable major-versions. |
| 7 | Crash dump forensics | **Pass** | F-5 — `bash.exe.stackdump` present at repo root, **untracked** (`.gitignore` catches `*.stackdump`). Content reviewed: only MSYS2 stack frames, no creds/paths leaked. Already documented in `harness/knowledge/cases/2026-04-22-msys2-bash-crash-llamacpp-build.md`. Recommend deletion. |

**Coverage: 7/7**

## Findings

### F-1 · Info · CWE-798 (informational) · pre-existing
**Where**: `Project/ZeroCommon/Llm/Providers/LlmProviderFactory.cs:25–26`
**What**: Hardcoded Webnori `BaseUrl` + `ApiKey` constants. Pre-existing,
explicitly architectural (free-tier shared credential for contributors,
documented in adoption notes).

**Voice impact**: `WebnoriGemmaStt` (new in `8fbcfcc`) is a second
consumer of the same constants. Does NOT make the existing risk worse,
but enlarges the blast radius if the key is ever rotated/revoked.

**Remediation**: None required for this review. Track in the existing
"shared key" architecture decision; revisit when Webnori key rotation is
discussed independently.

### F-2 · Low · CWE-256 Plaintext storage of credentials
**Where**: `%LOCALAPPDATA%\AgentZeroLite\voice-settings.json`,
written by `Project/ZeroCommon/Voice/VoiceSettingsStore.cs:39`.
**What**: `SttOpenAIApiKey` and `TtsOpenAIApiKey` persist as plaintext
JSON. File ACL is per-user (Windows default for `%LOCALAPPDATA%`), but
any process running as the user can read them.

**Voice impact**: Same risk profile as the existing `llm-settings.json`
which stores `OpenAIApiKey`. Voice introduces TWO additional plaintext
key slots (one for STT, one for TTS).

**Remediation (defer)**: Migrate to Windows DPAPI
(`ProtectedData.Protect` with `DataProtectionScope.CurrentUser`) for at
least the API-key fields. Should be a single sweep across all credential
slots in both `llm-settings.json` and `voice-settings.json` to keep the
abstraction consistent. Not blocking; raise as a separate hardening
ticket when scoped.

### F-3 · Low · A06 Vulnerable & Outdated Components
**Where**: `Project/AgentZeroWpf/AgentZeroWpf.csproj` (added `8fbcfcc`)
```xml
<PackageReference Include="System.Speech" Version="10.0.0-preview.3.25171.5" />
```
**What**: `System.Speech` is pinned to a `.NET 10 preview-3` version. The
project itself targets `net10.0-windows` (also preview), so the version
choice is consistent with the SDK, but a stable release of `.NET 10`
will ship a stable `System.Speech` and the project should track that.

**Remediation**: When `.NET 10` GA ships, bump `System.Speech` to the
stable counterpart in the same commit that updates `<TargetFramework>`
to the stable SDK. Add a tracking line under
`memory/project_gemma4_self_build_lifecycle.md` to surface this at GA
time. Not a release blocker today.

### F-4 · Info · Dead code — audio-write helper
**Where**: `Project/AgentZeroWpf/Services/Voice/VoiceCaptureService.cs:95`
```csharp
public static void WritePcmToWav(byte[] pcm, string path) { ... }
```
**What**: Public static method ported verbatim from origin. Origin
calls it from a "manual mode dump" path. Lite **never calls it** — grep
across `Project/` returns only the definition site.

**Voice impact**: Latent — a future contributor might wire it up to
something user-controlled (e.g. a debug "save last utterance" feature)
without realising the path is fully caller-supplied (no sanitization).

**Remediation**: Either (a) delete the method, or (b) gate it with a
hardcoded path under `%LOCALAPPDATA%\AgentZeroLite\voice-debug\` and
restrict callers. Cheap cleanup; do at next voice touch. Severity Info
because it's not currently exploitable.

### F-5 · Info · Untracked crash dump in repo root
**Where**: `D:\Code\AI\AgentZeroLite\bash.exe.stackdump` (1196 bytes,
mtime 2026-04-22 23:49)
**What**: MSYS2 `bash.exe` stack-frame dump from the 2026-04-22 build
incident. **Untracked** — `.gitignore` includes `*.stackdump`, verified.
Contents reviewed: only stack frames + module list (`bash.exe`,
`msys-2.0.dll`, `ntdll.dll`, `kernel32.dll`, `user32.dll`, etc.). **No
filesystem paths, no environment variables, no credentials in the
dump.**

**Recommendation**: Delete the file (`rm bash.exe.stackdump`). The
incident is already preserved in
`harness/knowledge/cases/2026-04-22-msys2-bash-crash-llamacpp-build.md`,
so the dump itself has no remaining diagnostic value.

### F-6 · Info · A08 Software & Data Integrity (model download)
**Where**: `Project/AgentZeroWpf/Services/Voice/WhisperLocalStt.cs:80–88`
**What**: First-time `EnsureReadyAsync` for an absent Whisper model
calls `WhisperGgmlDownloader.Default.GetGgmlModelAsync(ggmlType, ...)`
which fetches GGUF files from Hugging Face's CDN over HTTPS into
`%USERPROFILE%\.ollama\models\agentzero\whisper\`. Integrity is
guarded only by a min-bytes file-size check (`minBytes` in the catalog
table) — there is no SHA verification.

**Voice impact**: A compromised CDN endpoint or MITM (rare with TLS) could
serve a tampered GGUF; the first-load check would not catch it.
Practically, Whisper.net's `WhisperFactory.FromPath` would refuse
malformed binaries (and any tampering large enough to matter would also
break the format), but defence-in-depth would benefit from a published
SHA-256 in the catalog.

**Remediation (defer)**: Augment `LlmModelCatalogEntry`-style records
for Whisper models with `Sha256` fields and verify after download.
Origin doesn't do this either — accepted risk for this phase.

### F-7 · Info · Injection-surface negative result (verified)
**Where**: Voice subsystem (`Project/ZeroCommon/Voice/`,
`Project/AgentZeroWpf/Services/Voice/`,
`Project/AgentZeroWpf/UI/Components/SettingsPanel.Voice.cs`)
**What**: Verified by grep — voice files import zero of:
`ITerminalSession`, `ConPtyTerminalSession`, `Process.Start`, `cmd.exe`,
`powershell`, `Shell.*`, `terminal-send`, `SendInput`, `InjectKey`.
Voice transcripts (the most realistic injection vector — "Hey AI, run
`rm -rf`") flow only into `LlmGateway.OpenSession()` for LLM completion,
never back into a terminal. The headline project risk
(prompt-injection → command execution) is **not amplified** by this
work.

**Action**: Document this as the canonical assertion so future readers
can rely on it without re-running the grep.

## Severity calibration

- 0 Critical, 0 High, 0 Medium, 2 Low (F-2, F-3), 5 Info (F-1, F-4–F-7)
- All Lows are pre-existing class (plaintext-creds, preview-pin) or
  require additional design work (DPAPI sweep, .NET GA cutover) — none
  block release.
- **Release pipeline gate**: would PASS for `release-build-pipeline`
  invocation today. No Critical/High found.

## Self-evaluation (rubric)

| Axis | Score | Justification |
|---|:---:|---|
| Coverage (0–7) | **7/7** | All seven scope items touched; results tabulated. |
| Severity calibration (A–D) | **A** | Each finding pinned to OWASP/CWE or marked Informational; no vibes. |
| Actionability (A–D) | **A** | Every finding names file:line + concrete remediation (or explicit "defer" with reason). |

## Recommended actions (ranked)

1. **Now (1 min)**: `rm bash.exe.stackdump` (F-5 cleanup; documentation already exists).
2. **Next voice touch (5 min)**: delete or gate `WritePcmToWav` (F-4).
3. **Hardening sweep (30 min)**: DPAPI-protect the API-key fields in
   both `llm-settings.json` and `voice-settings.json` (F-2). Single
   commit, both files — keeps the storage convention consistent.
4. **At .NET 10 GA**: bump `System.Speech` to stable (F-3).
5. **Future**: SHA-256 verification on Whisper model downloads (F-6) —
   accepted risk for now; revisit if origin adopts this pattern first.

No remediation is **blocking**. The voice subsystem can ship as-is.
