# 2026-04-22 — MSYS2 `bash.exe` crash during llama.cpp / Gemma 4 build

> Status: **resolved** (per maintainer report; specific applied mitigation not
> captured at incident time — to be filled in below).
> Owner of analysis: `security-guard` (primary) with `build-doctor` correlation.
> Source artifact: `bash.exe.stackdump` left in repo root on 2026-04-22 23:49.
> The `.stackdump` file itself is gitignored and stays out of the repo.

---

## Context

The crash occurred during the **self-built `llama.dll` / `ggml-*.dll` workflow for
Gemma 4** documented in `Docs/llm/`. That workflow involves heavy native compilation
through `cmake` + `make` (or `Ninja`) typically driven from MSYS2 / Git Bash on
Windows. Bash crashed mid-build and dumped its stack to `bash.exe.stackdump` in the
project root. The build attempt failed; the artifact was the only signal left behind.

The maintainer reports the underlying issue has since been resolved, so this case
study exists to **preserve the diagnosis path** so the same pattern is recognized
quickly if it returns (especially with the next on-device model build that exercises
the same self-build loop — see `memory/project_gemma4_self_build_lifecycle.md`).

---

## Evidence (redacted stackdump excerpt)

```
Stack trace:
Frame         Function      Args
0007FFFFB920  00021005FEBA  msys-2.0.dll+0x1FEBA
0007FFFFB920  0002100467F9  msys-2.0.dll+0x67F9
0007FFFFB920  000210046832  msys-2.0.dll+0x6832
0007FFFFB920  000210068F86  msys-2.0.dll+0x28F86
0007FFFFB920  0002100690B4  msys-2.0.dll+0x290B4
0007FFFFBC00  00021006A49D  msys-2.0.dll+0x2A49D
End of stack trace
Loaded modules:
  bash.exe
  ntdll.dll, KERNEL32.DLL, KERNELBASE.dll
  USER32.dll, win32u.dll, GDI32.dll, gdi32full.dll, IMM32.DLL
  msys-2.0.dll
  msvcp_win.dll, ucrtbase.dll, advapi32.dll, msvcrt.dll
  sechost.dll, RPCRT4.dll, CRYPTBASE.DLL, bcryptPrimitives.dll
```

(Full args column dropped; argument values can leak pointers but no useful signal
here. Loaded module addresses dropped as a precaution — username paths can leak
through them in a more detailed dump.)

---

## Diagnosis

**Every frame in the stack trace lives inside `msys-2.0.dll`** with no application
code visible. `bash.exe` itself does not appear in the trace.

This is the textbook signature of an **MSYS2 fork-emulation crash**. MSYS2 (and
Cygwin) emulate POSIX `fork()` on Windows by replaying the parent's address space
into the child; this depends on consistent memory layout between processes. Anything
that perturbs that layout — antivirus DLL injection, ASLR variance, address-space
collision (BLODA-class), heap fragmentation in long-running shells, or just the
sheer load of a parallel native build — can corrupt the replay and crash inside
`msys-2.0.dll`.

No application code (`bash` proper, `make`, `cmake`, `gcc`, `ld`, `nvcc`, `vulkan`)
appears in the trace. That means:

- This is **not** a llama.cpp bug.
- This is **not** an AgentZero Lite bug.
- This **is** a build-environment instability triggered by what bash was doing,
  but not by what the build code is.

Loaded modules show only standard Windows + MSYS2 runtime DLLs. **No third-party
DLL injection** visible (no antivirus signature, no shell-extension hook). That
rules out the most common BLODA-style cause and points toward the heavier set of
causes — fork emulation pressure under parallel build load, or ASLR variance.

---

## Mitigation Considered (per `crash-dump-forensics.md` playbook)

| Mitigation | Applicability | Notes |
|------------|---------------|-------|
| `rebaseall` | Low | No DLL injection visible; classic BLODA fix unlikely to help |
| Reduce `make -j` parallelism | Medium | Cheap to try; would slow but stabilize |
| Switch build env to PowerShell + native CMake/Ninja | **High** | Avoids the MSYS2 fork path entirely; matches Microsoft-stewarded stack the rest of this project leans on |
| Antivirus exclusion for MSYS2 paths | Low | Module list shows no AV present; leaving here for completeness |
| Restart bash session between large builds | Low | Workaround, not fix |

The third option (PowerShell + Ninja) aligns with the project's broader posture
documented in `README.md` (project-concept section): *MS-managed channels for the
native niche*. Removing MSYS2 from the critical build path also reduces one more
non-MS-managed surface, which is a security-adjacent win — fewer fork-emulation
shims = fewer places where a bad day silently corrupts a build.

---

## Resolution

**Status**: maintainer-confirmed resolved during the Gemma 4 build effort.
**Specific applied mitigation**: *to be filled in by maintainer*.

> When the actual fix is known, replace this section with what was done so the
> next time this signature appears, the resolution is one click away.
> Likely candidate based on this project's MS-managed-stack posture: the build was
> moved off MSYS2 to PowerShell + native Ninja. Maintainer can confirm or amend.

---

## Generalize — what this case adds to the harness

1. **Crash-dump scope is now owned**: `security-guard` agent picked up
   "Crash dump forensics & redaction" as scope item #7 (see
   `harness/agents/security-guard.md`).
2. **Generic playbook seeded**: `harness/knowledge/crash-dump-forensics.md`
   — the triage flow, redaction rules, dump-type-by-type guidance.
3. **`*.stackdump` is in `.gitignore`** (verified). If a future contributor sees a
   stackdump tracked by git, that itself is a finding for `security-guard`.
4. **Build-environment correlation**: `build-doctor` cross-references this case
   when assessing the build pipeline — recurring stackdumps under build load are a
   signal that the build environment itself needs hardening, not the build script.

If the same fork-emulation signature appears for the **next** on-device model build
(per `project_gemma4_self_build_lifecycle.md`, the self-build loop is intentionally
preserved as a research path beyond this specific Gemma 4 instance), the diagnosis
above applies directly — head straight to the PowerShell + Ninja escape hatch.
