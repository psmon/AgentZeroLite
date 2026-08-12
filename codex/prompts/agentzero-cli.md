# agentzero-cli (Codex custom prompt)

Project-bundled Codex analog of the Claude skill `.claude/skills/agentzero-cli/`.
For internal CLI testing. Codex has no `skills/` auto-discovery, so this lives as
a custom prompt. To expose it as `/agentzero-cli`, copy or symlink this file into
`~/.codex/prompts/agentzero-cli.md` (official plugin packaging is separate).

Anti-drift: this file is a **pointer**, not a frozen copy. The authoritative,
version-matched usage is served by the live binary — always confirm there.

---

Drive the AgentZero Lite Windows shell through its `-cli` surface.
Windows-only. Prefer PowerShell.

## 1. Locate the exe (first, every session)

Not usually on PATH. Prefer the **Debug build (internal test)**:

1. `<repo>\Project\AgentZeroWpf\bin\Debug\net10.0-windows\AgentZeroLite.exe`
2. `<repo>\Project\AgentZeroWpf\bin\Release\net10.0-windows\AgentZeroLite.exe`
3. `%ProgramFiles%\AgentZeroLite\AgentZeroLite.exe` (Inno default)
4. `Get-Command AgentZeroLite.exe`

Helper: `powershell -File .claude/skills/agentzero-cli/scripts/find-cli.ps1`
(returns the path on its last line and prints build identity).

## 2. Invoke (Windows nuance)

`AgentZeroLite.exe` is a **GUI-subsystem binary** — a bare `& exe -cli` races on
console attach and often prints nothing. Use one of:

```powershell
& "<dir>\AgentZeroLite.ps1" <cmd> [args]                      # wrapper (beside the exe)
Start-Process $Exe -ArgumentList '-cli','<cmd>',... -NoNewWindow -Wait   # raw exe
```

To capture output for parsing, add `-RedirectStandardOutput out.txt` (Start-Process
can't pipe). Global flags: `--no-wait`, `--timeout <ms>`.

## 3. Load the real, current instructions

Ask the binary — it's version-matched and never drifts:

```powershell
Start-Process $Exe -ArgumentList '-cli','help' -NoNewWindow -Wait
Start-Process $Exe -ArgumentList '-cli','help','agentzero' -NoNewWindow -Wait
```

Repo-local deep references (read when orchestrating terminals):
- `.claude/skills/agentzero-cli/references/command-reference.md` — every command, GUI-needed flag.
- `.claude/skills/agentzero-cli/references/interaction-protocol.md` — handshake, `DONE()` reverse channel, discussion/debate loop.

## 4. Quick map

- **Terminals (GUI up):** `terminal-list` → `terminal-send <G> <T> "<text>"` → `terminal-wait <G> <T> --until done` → `terminal-read <G> <T> --last N`. Control keys via `terminal-key`.
- **Peer reply / handshake:** a peer sends back only by running, in its own terminal, `AgentZeroLite.exe -cli bot-chat "DONE(<text>)" --from <peerName>`. `--from` must match the tab identity or the broker drops it.
- **Native Windows control (no GUI):** `os list-windows|screenshot|element-tree|activate|dpi` (read-only); `os mouse-*|keypress` gated by `--allow-input` / `AGENTZERO_OS_INPUT_ALLOWED=1`.
- **No-GUI in-process:** `os *`, `worktree`, `cost`, `automation`, `version`.
- **IPC commands need the GUI** — if "GUI is not running", run `-cli open-win` and retry.
