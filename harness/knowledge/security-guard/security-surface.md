# Security Surface — Prompt Injection → OS Exec Map

> The injection-aware view of AgentZero Lite. `security-guard` reads this before every
> pass. Every entry below is a path where untrusted text can become an OS-visible action.

## Primary surface — AgentChatBot keystroke broker

`AgentChatBot` is the dockable chat pane that **forwards typed text or raw keystrokes to
the active terminal**. There are two modes:

- **CHT mode** — types text into the active ConPTY session.
- **KEY mode** — forwards raw keystrokes (Ctrl+C, arrows, Tab, Enter).

Either mode lets text from the bot side become input to whatever process owns the
terminal. If that process is `claude`, `codex`, `gh`, `pwsh`, etc., **the AI in that
terminal will act on the input as if the user typed it.**

Threat: a malicious prompt embedded in a doc, a tool result, or a remote MCP response
can route to the bot, the bot forwards it to a CLI, and the CLI runs a command.

Mitigations to look for:
- Is there an authorization checkpoint between bot input and terminal write?
- Is there length / pattern filtering for KEY mode payloads?
- Is there logging that captures what was forwarded, when, and to which terminal?

## Secondary surface — CLI ↔ GUI IPC

`AgentZeroLite.exe -cli <cmd>` posts `WM_COPYDATA` (marker `0x414C "AL"`) to the running
GUI. The GUI consumes the payload in `CliTerminalIpcHelper.cs` / `MainWindow`, dispatches
to handlers, and writes a JSON response to a memory-mapped file with the
`AgentZeroLite_*` prefix.

Threat surface:
- Any local process with the same user can post `WM_COPYDATA` to the GUI window.
- Any local process can open MMFs by name if it can predict the prefix.

Mitigations to look for:
- Strict marker validation on receive (not just length).
- Allowlist of CLI commands, not arbitrary string-to-handler dispatch.
- MMF naming includes a per-process random suffix or session-scoped token.
- Rate / concurrency limits to prevent flooding.

## Native binary trust

DLLs that ship with the app:
- `conpty.dll`, `Microsoft.Terminal.Control.dll` — from `CI.Microsoft.*` NuGet,
  copied via hard-coded `$(NuGetPackageRoot)` paths in `AgentZeroWpf.csproj`.
- `llama.dll`, `ggml-*.dll` — **self-built for Gemma 4**. Documented as a temporary
  workaround in `Docs/llm/index.md` and `memory/project_gemma4_self_build_lifecycle.md`.

Threat: if any of these are replaced (build pipeline drift, a tampered NuGet, or a
local file-replace), they execute with the app's privileges.

Mitigations to look for:
- Hash pinning or signature checks on load (currently not enforced — known gap).
- Build-time verification that copied paths resolve under expected versions
  (this is `build-doctor`'s job).
- Documentation that warns end-users (currently in README Security Notice — keep it
  there).

## Local data at rest

`%LOCALAPPDATA%\AgentZeroLite\agentZeroLite.db` (EF Core / SQLite). Holds:
- `CliDefinition` rows (built-in + user-added CLI launchers).
- (Future) workspace metadata, terminal history, etc.

Threat: any process running as the same user can read/write this file.

Mitigations to look for:
- Built-in `CliDefinition` rows (`IsBuiltIn = true`) are not deletable from UI.
- Migrations are deterministic and live in `Project/ZeroCommon/Data/Migrations/` only.
- No secrets / credentials are persisted in this DB (verify on every diff).

## Single-instance enforcement

Named mutex `Local\AgentZeroLite.SingleInstance` guards single-instance GUI startup.

Threat: if the mutex is left held (e.g. crash without cleanup, or the Akka shutdown
deadlock that previously occurred), the next launch silently waits — user sees nothing.

Mitigations to look for:
- `coordinated-shutdown.exit-clr = on` in Akka config — terminates CLR from shutdown
  phases, not from user code (this is the historical fix; do not regress).
- Crash path releases the mutex.

## Workspace and terminal name handling

User-supplied workspace and terminal names become Akka actor paths (`/user/stage/ws-<name>`).
Akka rejects `/`, `:`, spaces, etc.

Mitigation: every site that takes user-supplied names must route through
`ActorNameSanitizer` before using them in path construction. Missed sites are findings.

## Markdown / Pencil rendering

- Markdown preview uses `MarkdownViewer` + `MermaidRenderer` + WebView2 with embedded
  `Assets/mermaid.min.js`. WebView2 means HTML/JS execution context — verify CSP if any
  remote content can land in the markdown source.
- `.pen` files are encrypted and routed through the `pencil` MCP server only — they
  never go through `Read`/`Grep`.

## What is NOT in scope here

- Network attacks against external CLIs (claude.ai, codex, gh) — those CLIs own their
  own auth. Scope is what AgentZero Lite *does with* their I/O.
- Physical access to the host — out of threat model.
