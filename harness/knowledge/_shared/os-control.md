# OS Control Surface (M0014) — CLI verbs + LLM tools

> Status: binding (mission M0014, 2026-05-07).
> Owner: tamer; specialists: code-coach, security-guard.
> Engine: `harness/engine/os-cli-e2e-smoke.md`.

## Why this knowledge file exists

AgentZero Lite gained Windows desktop automation in mission M0014. The
surface is symmetrical — every read-only OS-control capability is
exposed both as a CLI verb (`AgentZeroLite.exe -cli os <verb>`) and as
an LLM function call (`os_<name>` in the agent loop's tool catalog).
This file is the single source of truth that downstream agents use:
which verbs exist, what they accept, what they return, and where the
approval gate sits.

The implementation lives at `Project/AgentZeroWpf/OsControl/` and the
LLM bridge is in `Project/AgentZeroWpf/Services/WorkspaceTerminalToolHost.cs`.

## Surface map

| CLI verb | LLM tool | Class of side-effect | Gate |
|---|---|---|---|
| `os list-windows [--filter S] [--include-hidden]` | `os_list_windows` | enum, read-only | none |
| `os get-window-info <hwnd>` | (CLI only) | read-only | none |
| `os screenshot [--hwnd N] [--color] [--full]` | `os_screenshot` | GDI BitBlt → PNG file | none (data on disk only) |
| `os element-tree <hwnd> [--depth N] [--search S]` | `os_element_tree` | UIA tree walk | none |
| `os text-capture <hwnd>` | (CLI only, Phase A) | UIA `Name` aggregation | none |
| `os dpi` | (CLI only) | metrics read | none |
| `os activate <hwnd>` | `os_activate` | Z-order change | none |
| `os mouse-click <x> <y> [...]` | `os_mouse_click` | **input simulation** | **gated** |
| `os mouse-move <x> <y>` | (CLI only, Phase B) | **input simulation** | **gated** |
| `os mouse-wheel <x> <y> <delta>` | (CLI only, Phase B) | **input simulation** | **gated** |
| `os keypress <spec>` | `os_key_press` | **input simulation** | **gated** |
| `os audit [--last N]` | (CLI only) | inspect tmp/os-cli/audit/ | n/a |

## Approval gate

Two independent enabler signals — either is sufficient to unblock input
simulation. Read-only verbs are unconditional.

1. **Per-call CLI flag**: append `--allow-input` to a `mouse-*` or
   `keypress` invocation. Not persisted; must repeat per command.
2. **Process env var**: set `AGENTZERO_OS_INPUT_ALLOWED=1` (any of `1`,
   `true`, `yes`, case-insensitive). The GUI Settings panel is the
   intended way to toggle this for the running app. CI / unattended
   scripts can set it directly. Process-scoped — does not survive a
   restart.

There is **no per-call human confirmation**. The gate is binary. This
matches the AgentWin (Origin) coarse model but tightens it: Origin gated
nine LLM tools behind one settings checkbox; Lite ships with a stricter
default (off) plus a per-CLI-call flag for ad-hoc operator runs.

When a gated tool is denied, callers receive:

```json
{
  "ok": false,
  "error": "OS input simulation is gated. Set environment variable AGENTZERO_OS_INPUT_ALLOWED=1 (LLM/GUI) or pass --allow-input on the CLI verb to enable.",
  "verb": "<tool name>"
}
```

LLM-side instruction (codified in `AgentToolGrammar.SystemPrompt`): if
gate denied, do **not** retry — report the error to the user and call
`done`. The system prompt explicitly forbids the model from looping on
gate denials.

## Audit log

Every OS-control call appends one JSONL line to:

```
tmp/os-cli/audit/<yyyy-MM-dd>.jsonl
```

Schema:

```jsonc
{
  "ts": "2026-05-07T11:42:09.123+09:00",
  "caller": "cli" | "llm" | "e2e",
  "verb": "list_windows" | "screenshot" | "mouse_click" | …,
  "args": { … },           // shape varies per verb
  "ok": true | false,
  "error": null | "…"      // populated when ok=false
}
```

Audit is best-effort: failures inside `OsAuditLog.Record` swallow the
exception so a transient disk error never breaks the actual operation.
The trail is a forensic record after the fact, not a transactional gate.

## Output paths

```
tmp/os-cli/
├── audit/                   one JSONL file per day
│   └── 2026-05-07.jsonl
├── screenshots/             one folder per day, one PNG per capture
│   └── 2026-05-07/
│       └── 11-42-09-456-desktop.png
└── e2e/                     E2E smoke summary log per day
    └── 2026-05-07.log
```

The E2E smoke script itself is tracked in git at
`Docs/scripts/launch-self-smoke.ps1` — `tmp/` is gitignored, so the
script lives outside it. Runtime artefacts (PNGs, audit JSONL, e2e
logs) stay under `tmp/os-cli/` because they're operator-local and
shouldn't pollute the repo.

The repo root is detected by walking up from `AppContext.BaseDirectory`
looking for `.git/` or `AgentZeroLite.sln`. When the binary runs from an
installed location, artefacts fall back to
`%LOCALAPPDATA%\AgentZeroLite\tmp\os-cli\`.

## Screenshot encoding policy

- **Format**: PNG only.
- **Default colorimetry**: grayscale (smaller files, fine for diagnostic
  use and LLM consumption — matches Origin default).
- **Default downscale**: full-desktop captures clamp to 1920×1080 if
  larger; per-window captures preserve native resolution.
- **Return shape**: callers receive the file path, not the bytes. The
  LLM never sees image data on its context window.

## Anti-patterns (do NOT do)

- **Reading screenshots into LLM context as base64.** The agent loop's
  toolbelt returns a path string; the user (or downstream tool) opens
  the file. Inlining bytes wastes tokens and exposes screen content to
  the prompt-injection attack surface.
- **Auto-launching the app from an LLM tool.** `os_activate` requires a
  pre-existing `hwnd`. There is no `os_launch` LLM tool by design — the
  CLI has `open-win`, but exposing it to the LLM creates a recursion
  risk (LLM driving an instance of itself). If a follow-up mission
  needs this, gate it behind its own approval flag.
- **Reusing screenshot paths.** Filenames embed millisecond precision
  (`HH-mm-ss-fff`) so two near-simultaneous captures don't collide.
  Don't truncate to seconds.
- **Skipping audit on perceived "trivial" calls.** Every entry is a
  forensic record; even read-only `os_list_windows` should log because
  enumeration is itself a fingerprinting signal.

## Cross-references

- Engine: `harness/engine/os-cli-e2e-smoke.md`
- Implementation entry points:
  - `Project/AgentZeroWpf/OsControl/OsControlService.cs` (facade)
  - `Project/AgentZeroWpf/OsControl/OsCliCommands.cs` (CLI dispatch)
  - `Project/AgentZeroWpf/Services/WorkspaceTerminalToolHost.cs` (LLM bridge)
- Grammar surface: `Project/ZeroCommon/Llm/Tools/AgentToolGrammar.cs` (KnownTools, GBNF)
- Origin comparison: `Docs/agent-origin/03-adoption-recommendations.md` §P1-2
