---
date: 2026-04-25T16:00:00+09:00
agent: security-guard
type: review
mode: log-eval
trigger: "보안점검 전체 첫 수행진행"
scope: full-repo (HEAD)
---

# Security Guard — Baseline Full Pass

First baseline run of the security review. Touched all 7 scope items per
`harness/agents/security-guard.md`. Cross-references:
[`security-surface.md`](../../knowledge/security-surface.md),
[`agentzerolite-architecture.md`](../../knowledge/agentzerolite-architecture.md).

## Summary

| Severity | Count |
|----------|-------|
| Critical | 0 |
| High     | 0 |
| **Medium** | **2** |
| Low      | 4 |
| Info     | 2 |

**Release-build gate decision**: **PASS** (no Critical, no High → does not block
`release-build-pipeline`). Two Medium findings should be triaged before next
significant release but do not warrant blocking the gate.

---

## Findings

### F-001 — Medium — IPC: WM_COPYDATA accepts commands from any local same-user process

**Scope**: #3 (CLI ↔ GUI IPC) and #1 (injection surface)
**OWASP/CWE**: CWE-862 (Missing Authorization), CWE-749 (Exposed Dangerous Method)
**File:line**: `Project/AgentZeroWpf/UI/APP/MainWindow.xaml.cs:347-358` (WndProc),
`Project/AgentZeroWpf/UI/APP/MainWindow.xaml.cs:365-426` (HandleCliCommand)

**Evidence**:
```csharp
// MainWindow.xaml.cs:347-358
private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
{
    if (msg == (int)NativeMethods.WM_COPYDATA)
    {
        var cds = Marshal.PtrToStructure<NativeMethods.COPYDATASTRUCT>(lParam);
        if (cds.dwData == (IntPtr)CYCOPYDATA_COMMAND && cds.cbData > 0)
        {
            string json = Marshal.PtrToStringUTF8(cds.lpData, cds.cbData) ?? "";
            HandleCliCommand(json);    // <— no sender PID check
            handled = true;
            return (IntPtr)1;
        }
    }
    ...
}
```

**Root cause**: The marker `0x414C` is the only gate. Any local process running as
the same user can `FindWindow("AgentZero Lite")` → `SendMessage(WM_COPYDATA)` with
that marker, then issue `terminal-send`/`terminal-key` to write arbitrary text or
keystrokes into whichever ConPTY tab is active (claude, codex, gh, docker, pwsh,
etc.). The CLI in that terminal then acts on the input.

This is the materialization of the surface the README Security Notice flags. It
is **by design** — the local CLI experience requires this — but currently
**unmitigated at the code level**.

**Remediation suggestions** (one of the three is enough):
1. **Sender process allowlist**: extract sender PID via
   `GetWindowThreadProcessId(hwnd, out var senderPid)` (or use
   `WTSQuerySessionInformation`), and verify the sender process matches one of:
   - The current process (CLI mode reaching back into GUI mode wrapper),
   - A child of `AgentZeroLite.exe`,
   - The paired `AgentZeroLite.ps1` host (`pwsh.exe` / `powershell.exe` whose
     command line includes `AgentZeroLite.ps1`).
2. **Per-session token**: GUI generates a random token at startup, writes it to
   a per-user named MMF (`AgentZeroLite_Session_{userSid}_Token`) read-only to
   user. CLI / `.ps1` reads the token and sends it as a JSON field on every
   command. WndProc rejects commands without the matching token.
3. **Per-command UX confirmation**: any `terminal-send`/`terminal-key`/`bot-chat`
   from an unrecognized PID surfaces a one-shot toast/modal asking the user to
   approve. After approve, optionally remember the PID for the session.

Option 1 is the cheapest and matches the project's "MS-managed-stack, no extra
deps" posture. Option 2 has the strongest formal guarantee. Option 3 is the most
visible to the user but adds friction.

---

### F-002 — Medium — Dependency: EF Core 10.0.0-preview shipped in production builds

**Scope**: #6 (dependency drift)
**OWASP/CWE**: CWE-1357 (Reliance on Insufficiently Trustworthy Component)
**File:line**: `Project/ZeroCommon/ZeroCommon.csproj:15-19`,
`Project/AgentZeroWpf/AgentZeroWpf.csproj:32-36`

**Evidence**:
```xml
<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.0-preview.3.25171.6" />
<PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.0-preview.3.25171.6">
```

**Root cause**: Both projects depend on `EntityFrameworkCore.Sqlite` and
`EntityFrameworkCore.Design` at a **preview** version. EF Core preview releases
have shipped breaking schema/migration semantics between previews in the past;
end-users updating from one AgentZero Lite release to the next could see local
DB (`%LOCALAPPDATA%\AgentZeroLite\agentZeroLite.db`) corruption or migration
failure if the preview pinning isn't kept in lockstep with what generated their
existing migrations.

The .NET 10 platform itself is preview, so this is partially intrinsic. But EF
Core preview is its own additional risk surface.

**Remediation suggestions**:
- Pin to a single preview build per AgentZero Lite release, **document it** in
  the release notes (so users can read what to expect).
- Switch to GA EF Core 9.0.x against `net10.0` (verify compatibility) until EF
  Core 10 ships GA — would remove the preview-of-preview risk stack entirely.
- Add a startup check in `AppDbContext.InitializeDatabase()` that detects model
  drift (`Database.EnsureCreated()` vs `Migrate()`) and surfaces a clear error
  rather than silently corrupting the DB.

Note: `ZeroCommon.csproj:24-29` already pins `Microsoft.Build.Tasks.Core`
(NU1903 / GHSA-h4j7-5rxr-p4wc, High) and `System.Security.Cryptography.Xml`
(GHSA-37gx, GHSA-w3x6) explicitly with comments. **This is exactly the right
pattern** — extending it to EF Core is consistent with project posture.

---

### F-003 — Low — Native: self-built llama.dll/ggml-*.dll glob with no signature/hash check

**Scope**: #4 (native binary trust)
**OWASP/CWE**: CWE-494 (Download of Code Without Integrity Check) — *acknowledged*
**File:line**: `Project/ZeroCommon/ZeroCommon.csproj:42-51`

**Evidence**:
```xml
<Content Include="runtimes\win-x64-cpu\native\*.dll">  <!-- glob, no version/hash -->
<Content Include="runtimes\win-x64-vulkan\native\*.dll">
```

**Root cause**: Wildcard-glob picks up any `.dll` placed in those folders and
ships it to output. No signature pinning, no hash check at load. Anyone with
write access to the `runtimes/` folders during a build (a tampered PR, a
compromised dev machine) can substitute the native code that runs in process.

**Why Low** (not Medium):
- Acknowledged in `Docs/llm/index.md` ⚠️ banner and the README Security Notice
  ("self-built DLLs not vouched for; build it yourself").
- `memory/project_gemma4_self_build_lifecycle.md` documents the temporary
  nature of this path.
- Mitigation has explicit owner: end-users are told to either trust the
  maintainer or self-build.

**Remediation suggestions** (improvements, not blockers):
- Pin the exact filenames in csproj (`llama.dll`, `ggml.dll`, `ggml-base.dll`,
  `ggml-cpu.dll`, `ggml-vulkan.dll`) with explicit `<Content Include>` per file
  rather than `*.dll` glob. Stops accidental inclusion of stray DLLs.
- Compute & log hashes at first load for each DLL (as `[LLM-NATIVE] sha256=…`);
  changes between releases become visible in user app-log.
- When LLamaSharp NuGet ships Gemma 4, **rip this whole subsystem out** per
  `memory/project_gemma4_self_build_lifecycle.md`.

ConPTY DLL pinning is fine: explicit version segments at
`AgentZeroWpf.csproj:103-110` (`ci.microsoft.windows.console.conpty/1.22.250314001`,
`ci.microsoft.terminal.wpf/1.22.250204002`) match the `<PackageReference>`
versions on lines 22-23. ✓

---

### F-004 — Low — Latent injection: `RunShellAsync(string command)` interpolates raw into `cmd /c`

**Scope**: #1 (injection surface)
**OWASP/CWE**: CWE-78 (OS Command Injection — latent / not currently exploitable)
**File:line**: `Project/AgentZeroWpf/UI/APP/AgentBotWindow.xaml.cs:1138-1163`

**Evidence**:
```csharp
private static async Task<(int ExitCode, string Output)> RunShellAsync(string command)
{
    var psi = new System.Diagnostics.ProcessStartInfo
    {
        FileName = "cmd.exe",
        Arguments = $"/c {command}",   // <— raw interpolation
        ...
    };
    ...
}
```

**Why Low**: Currently only called with **hardcoded literal strings**:
- `RunShellAsync("where claude")` (line 579)
- `RunShellAsync("where AgentZeroLite.ps1")` (line 1040)
- `RunShellAsync("where AgentZeroLite.exe")` (line 1041)

Not currently exploitable. **But the API shape invites future bugs** — any
future caller passing user/AI text gets command injection for free.

**Remediation suggestions**:
- Tighten the signature: `RunShellAsync(string filename, params string[] args)`
  using `ProcessStartInfo.ArgumentList` (which auto-quotes per Win32 rules).
- Or replace the three callers with a purpose-built `WhereAsync(string toolName)`
  helper that validates `toolName` is `[A-Za-z0-9._-]+`.
- Or add a private overload guard: `RunShellAsync` requires the caller to opt
  into "I promise this is a literal" via a sentinel parameter.

---

### F-005 — Low — Path quoting: `"` in user-side filename can break out of arg quoting

**Scope**: #1 (injection surface)
**OWASP/CWE**: CWE-77 (Command Injection via path quoting)
**File:line**:
- `Project/AgentZeroWpf/UI/Components/FileTreePanel.xaml.cs:339,341`
- `Project/AgentZeroWpf/UI/Components/DocumentViewerPanel.xaml.cs:488,494`

**Evidence**:
```csharp
// FileTreePanel.xaml.cs:339
System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
// DocumentViewerPanel.xaml.cs:488
Process.Start(new ProcessStartInfo(_edgePath.Value, $"\"{_currentFile}\"") { UseShellExecute = false });
```

**Root cause**: Inline `$"\"{path}\""` style argument building. If `path` /
`_currentFile` contains a `"`, the quoting breaks and downstream argv parsing
sees additional tokens. NTFS allows `"` in directory and file names (rarely
used in practice, but technically possible).

**Why Low**:
- Source of `path` / `_currentFile` is the local file system, surfaced by user
  right-click on items they navigated to themselves.
- No remote/AI-controlled write path produces these inputs.
- Even if exploited, attacker-supplied tokens land as args to `explorer.exe`,
  Edge, or Chrome — not arbitrary executables.

**Remediation**: Use `ProcessStartInfo.ArgumentList.Add(path)` — automatic
correct quoting per Win32 rules, no manual `\"` needed.

---

### F-006 — Low — UI: phishing URLs reach clickable surface via chat injection

**Scope**: #1 (injection surface) and #3 (IPC chain)
**OWASP/CWE**: CWE-1021 (Improper Restriction of Rendered UI Layers)
**File:line**:
- Origin: `Project/ZeroCommon/Services/AgentEventStream.cs:38-40,118-130` (URL regex)
- Display: `Project/AgentZeroWpf/UI/APP/AgentBotWindow.xaml.cs:284-379`
  (AddUrlBubble, OpenUrl)

**Evidence**: URL regex constrains to `https?://...` and `localhost:NNNN`
(scheme-restricted ✓), so `OpenUrl(url)` with `UseShellExecute=true` will only
hand `http(s)://` URLs to the default browser — which is safe in itself. **But**
any chat-injected message (via `bot-chat` IPC, F-001 chain) containing an
`https://attacker.evil.com/?token=…` URL reaches a clickable bubble in the user's
chat panel. The user could be social-engineered into clicking.

**Why Low**:
- The actual `Process.Start` call is browser-safe (regex gate is correct).
- Phishing risk is generic to any chat surface that renders URLs.

**Remediation suggestions** (UX hardening, not security-critical):
- Show the **host portion** of the URL conspicuously in the bubble so users can
  see `evil.com` vs `legitimate.com` at a glance.
- Color-code "first-seen-this-session" hosts vs "frequent" hosts.
- For IPC-arrived chats from PIDs not in the F-001 allowlist, consider stripping
  URLs to plain text (no clickable bubble).

---

### F-007 — Info — Stability: ActorNameSanitizer hash uses `string.GetHashCode()` (non-deterministic across processes)

**Scope**: #2 (actor name sanitization)
**File:line**: `Project/ZeroCommon/Actors/ActorNameSanitizer.cs:33`

**Evidence**:
```csharp
if (hadReplacement)
    sb.Append('-').Append(name.GetHashCode().ToString("x8"));
```

**Note**: .NET randomizes string hashes per process for security
(prevents hash-collision DoS). This means a workspace name like `프로젝트 A`
sanitizes to `_____-abcd1234` in one process but `_____-9876fedc` in the next.

**Security**: Not a finding (security side: nil). The sanitization itself is
strict and correct (only `[A-Za-z0-9_.\-]` retained, prefix-collision avoided
by hash).

**Reliability**: Means actor paths for non-ASCII workspace names are not stable
across restarts. If anything in the codebase depends on actor path equality
across process restarts (logs, state files, IPC), it would break. Spot-check
suggests nothing currently does, but worth a flag.

**Remediation** (optional): use `XxHash64.HashToUInt64(MemoryMarshal.AsBytes(name.AsSpan()))`
or SHA1-truncated for stable, deterministic hashing.

---

### F-008 — Info — Persistence boundary verified clean

**Scope**: #5 (persistence)
**File:line**: `Project/AgentZeroWpf/Data/Migrations/` (empty as expected)

`Project/AgentZeroWpf/Data/Migrations/` exists but is empty — no scaffolds
present. `git ls-files Project/AgentZeroWpf/Data/Migrations/` returns empty.
The CLAUDE.md guidance ("migrations live in `Project/ZeroCommon/Data/Migrations/`,
do not scaffold into the WPF folder") holds. ✓

The single migration `20260421061855_InitialCreate.cs` is correctly in
`Project/ZeroCommon/Data/Migrations/`. Built-in `CliDefinition` rows seeded
in `AppDbContext.OnModelCreating` (CMD/PW5/PW7 with `IsBuiltIn = true`) +
`EnsureDefaultCliDefinitions` (Claude row, also `IsBuiltIn = true`) — UI
deletion gating is preserved. ✓

---

## Scope coverage check

| # | Scope item | Covered by | Status |
|---|------------|------------|--------|
| 1 | Injection surface | F-001, F-004, F-005, F-006 | ✓ |
| 2 | Actor name sanitization | F-007 (info), structural verification | ✓ |
| 3 | CLI ↔ GUI IPC | F-001 | ✓ |
| 4 | Native binary trust | F-003 (self-built), ConPTY pinning verified | ✓ |
| 5 | Persistence | F-008 (clean) | ✓ |
| 6 | Dependency drift | F-002 (EF Core preview); positive pinning noted | ✓ |
| 7 | Crash dump forensics | `git ls-files` clean, `*.stackdump` ignored | ✓ |

**Coverage = 7/7.**

## Positive observations (worth keeping)

- `ZeroCommon.csproj:24-29` proactively pins `Microsoft.Build.Tasks.Core` and
  `System.Security.Cryptography.Xml` against named CVEs with inline justification
  comments. This is the model pattern — extend it to EF Core.
- `AgentEventStream.UrlRegex` is scheme-restricted (`https?://...`), which keeps
  `OpenUrl` from being abused as an arbitrary-`Process.Start` primitive.
- The `ITerminalSession` seam is respected: `ZeroCommon` has the interface, only
  `AgentZeroWpf` implements it. `ZeroCommon.Tests` does not pull in WPF.
- Native DLL pinning for ConPTY/Microsoft.Terminal is exact (matching version
  segments between `<PackageReference>` and `<Content Include>`).
- README Security Notice + `Docs/llm/` disclaimer correctly frame end-user trust
  expectations for the self-built DLL path.

## Self-evaluation (rubric)

| Axis | Measure | Result |
|------|---------|--------|
| Coverage | Did the pass touch every one of the 7 scope items? | **7 / 7** |
| Severity calibration | Findings tied to OWASP/CWE refs, not vibes | **A** — all 8 findings reference CWE / OWASP where applicable; no vibes |
| Actionability | Each finding names file:line + concrete fix | **A** — all findings include file:line and at least one concrete remediation path |

## Release-build pipeline gate decision

**PASS**. No Critical, no High. The two Medium findings (F-001 IPC sender
verification, F-002 EF Core preview) should be triaged before the next
significant release but do not block this gate per
`harness/engine/release-build-pipeline.md` rules.

If `agent-zero-build` is invoked within the next 24 hours, the engine may treat
this log as the satisfied pre-step.

## Recommended next steps (ordered)

1. **F-001 mitigation** — add sender PID verification in WndProc (Option 1
   from F-001). Smallest delta, biggest blast-radius reduction.
2. **F-004 fix** — tighten `RunShellAsync` signature; mechanically refactor the
   3 literal callsites. Prevents future regression.
3. **F-002 triage** — decide whether to fork an EF Core 9 GA path or document
   the preview pinning expectation in release notes.
4. **F-003 hardening** — pin native DLL filenames (no glob) and log hashes at
   load. Cheap, makes drift visible.
5. **F-005 fix** — convert `Process.Start` arg-string callers to
   `ArgumentList.Add(...)`. Clean up while in the file.
6. Re-run `security-guard` after F-001 lands to confirm the gate stays green
   with the mitigation in place.
