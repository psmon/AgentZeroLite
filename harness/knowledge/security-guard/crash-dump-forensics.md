# Crash Dump Forensics — Playbook

> Owner: **`security-guard`** (primary) — crash dumps are memory-forensic artifacts
> with potential credential/path leak risk and can also be exploit signals.
> Cross-reference: **`build-doctor`** (secondary) — when a dump appears in a build
> context, the build-environment correlation matters.

This playbook covers the dump types most likely to appear in this repo's working tree.

---

## Dump types we expect to see

| Extension | Source | Format | Notes |
|-----------|--------|--------|-------|
| `*.stackdump` | Cygwin / MSYS2 / Git Bash | Plain-text stack frame list | Most common — bash crashes during builds |
| `*.dmp` | Windows minidump | Binary (PE format) | Produced by WER / `MiniDumpWriteDump` — needs WinDbg / dotnet-dump |
| `core` / `core.*` | POSIX coredump (rare on Windows) | Binary ELF | Should not normally appear; flag if seen |
| `crash-*.log` | LLamaSharp / llama.cpp custom | Text + native trace | Appears under `Docs/llm/` work — check there |

**`.gitignore` policy**: all four patterns are ignored — these files **must not** be
committed. If you find any in `git ls-files`, that is itself a finding.

---

## Triage flow (apply to any dump)

```
dump file appears
       │
       ▼
1. Identify type (extension + magic)
       │
       ▼
2. Classify exposure: does it contain process memory? (yes for *.dmp; partial for stackdump)
       │
       ▼
3. Redact before sharing — see "Redaction" below
       │
       ▼
4. Diagnose (technical) — see per-type sections
       │
       ▼
5. Correlate with build / runtime context (ask build-doctor if build-time)
       │
       ▼
6. Decide: ignorable / mitigation / case-study-worthy
       │
       ▼
7. If case-study-worthy → write to harness/knowledge/cases/{date-slug}.md
```

---

## `*.stackdump` — Cygwin / MSYS2 / Git Bash

### What it is

Plain-text stack frame list dumped to disk when `bash.exe` (or any Cygwin/MSYS2
binary) crashes. Contents:

- A `Stack trace:` block with `Frame / Function / Args` columns. Each row is a
  return address into a loaded module + `(arg0, arg1, arg2, arg3)`.
- A `Loaded modules:` block listing the address each DLL was loaded at.

### What it is NOT

- It is **not** a full memory dump. No heap/stack contents — just RIP frames.
- It is **not** a Windows minidump (`*.dmp`). Cannot be opened in WinDbg directly.
- Therefore the credential/key leak risk is much lower than a `.dmp`, but **paths
  still leak** (loaded DLL paths, module load addresses).

### Reading the trace

1. Look at the **topmost frame** — that's where the crash occurred.
2. Frames are `<module>.dll+<offset>` form. If every frame is inside `msys-2.0.dll`,
   it is almost certainly a **fork emulation crash** — Cygwin/MSYS2 fork is fragile
   and frequently fails when something perturbs memory layout.
3. Check the loaded module list for unexpected DLLs — antivirus injection, third-party
   shell extensions, custom hooks. Each is a potential cause.

### Common causes (MSYS2 / Cygwin fork crash)

| Cause | Signal | Mitigation |
|-------|--------|------------|
| Address space layout collision (BLODA) | Repeated crashes from same parent process; AV present in module list | `rebaseall`, exclude AV from MSYS2 paths |
| Heavy parallel make exhausting fork limits | Crash during `make -jN` with large N | Reduce `-j`, use native CMake + Ninja |
| Antivirus DLL injection | Unfamiliar DLL in `Loaded modules:` | Add MSYS2 / Git Bash dirs to AV exclusions |
| Long-running shell session memory fragmentation | Crash after many minutes inside same bash | Restart bash; switch to PowerShell for the build step |
| Path/quoting in a spawned tool | Crash during specific tool invocation | Run the tool directly outside bash to confirm |

### Mitigation: build-environment escape hatches (this repo)

- `cmake --preset` + `Ninja` directly from PowerShell avoids bash fork entirely.
- For self-built `llama.dll` work specifically, the documented route is via
  `Docs/llm/` — note any future updates if a recommended build env shifts.

---

## `*.dmp` — Windows Minidump

### What it is

Binary minidump (PE-style header). Created by `MiniDumpWriteDump`, Windows Error
Reporting (WER), or `procdump`. Can contain:

- Full process memory (if MiniDumpWithFullMemory) — **high leak risk**.
- Module list, thread list, exception record.
- Handle table, environment block.

### Tools

- **WinDbg** (Microsoft Store) — primary analysis tool. `!analyze -v` is the start.
- **dotnet-dump** — for managed (.NET) crashes: `dotnet-dump analyze <file>`.
- **dump-symbol-server** — symbols for Microsoft DLLs:
  `srv*c:\symbols*https://msdl.microsoft.com/download/symbols`.

### When the AgentZero Lite app crashes

If `AgentZeroLite.exe` produces a `.dmp` (look in `%LOCALAPPDATA%\CrashDumps\` and
the working directory):

1. Verify the crash is reproducible.
2. Run `dotnet-dump analyze <file>` first — `clrstack` and `pe` for the exception.
3. Look for native frames involving `conpty.dll`, `Microsoft.Terminal.Control.dll`,
   `llama.dll`, `ggml-*.dll` — these are the highest-risk surfaces in this app.
4. If the native frames are inside the self-built llama bundle, cross-check
   `memory/project_gemma4_self_build_lifecycle.md` — was a stale build still on disk?
5. Redact, write a case study under `harness/knowledge/cases/`.

---

## Redaction (always before sharing externally)

Even a stackdump can leak:

- **Username** in the loaded module paths (`C:\Users\<name>\...`).
- **Project layout** (build dir paths revealing private repo names).
- **DLL fingerprints** (which exact patched build was loaded).

Minidumps additionally leak:

- **Environment variables** (often contain `*_TOKEN`, `*_KEY`, `*_SECRET`).
- **Stack contents** (in-memory secrets, JWTs, recently-typed text).
- **Heap contents** (anything the process touched).

Redaction rules:

1. Replace `C:\Users\<name>\` with `~\` in any text dump before sharing.
2. For minidumps, **never** share the raw file outside the trusted boundary. Share
   the `!analyze -v` output instead, with paths and env vars redacted.
3. If a dump is bundled into a case study under `harness/knowledge/cases/`, apply
   the rules above to whatever excerpts go into the doc. The raw file stays
   gitignored and out of the repo.

---

## Case study format

When a dump turns out to be worth keeping (recurring, instructive, or load-bearing
for the project's history), write it to `harness/knowledge/cases/{yyyy-MM-dd}-{slug}.md`:

```markdown
# {Date} — {one-line title}

## Context
Where the crash happened in the workflow (build / runtime / IDE attach / etc.)

## Evidence
Redacted stackdump excerpt OR `!analyze -v` summary.

## Diagnosis
What the trace shows (which module, which class of crash).

## Mitigation Considered
Options the playbook lists.

## Resolution
What actually fixed it (filled in by the user when the fix is known).

## Generalize
What part of this case becomes a future-proofing rule (knowledge update,
agent scope addition, ignored from now on, etc.)
```

This format keeps cases comparable and helps `tamer` spot repeat patterns later.
