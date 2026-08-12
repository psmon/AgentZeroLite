---
name: agentzero-cli
description: |
  Drive the AgentZero Lite Windows shell from any terminal via its `-cli`
  command surface. Use this whenever a task involves: locating the
  AgentZeroLite.exe binary, controlling terminal tabs inside a running
  AgentZero GUI (list/read/send/key/wait), talking to OTHER agent terminals,
  the terminal handshake / DONE() reverse channel, multi-agent discussion or
  debate between tabs, git worktrees, orchestration, automation schedules,
  token cost, or native Windows control (window enum, screenshot, UI tree,
  mouse/keyboard input). Triggers on English "use the AgentZero CLI",
  "control the terminal tab", "send to the other agent", "handshake with the
  terminal", "screenshot a window", and Korean "에이전트제로 CLI",
  "터미널이랑 대화해", "터미널 핸드쉐이크", "다른 터미널에 보내",
  "터미널끼리 토론", "윈도우 제어해줘", "창 스크린샷 찍어".
  Windows-only. Prefer PowerShell. The `os` (native-control) verbs work with
  no GUI running; every other command needs the AgentZero Lite GUI up.
---

# AgentZero Lite — CLI control surface

`AgentZeroLite.exe` is **one binary, two modes**. Normally it launches the WPF
GUI. When its args contain `-cli`, `CliHandler` takes over, runs the command,
prints to the console, and exits — the GUI message loop never starts. This
skill is the operator's manual for that CLI: the parts `-cli help` cannot teach
you (where the exe lives, how to invoke it reliably on Windows, and how
terminals talk to each other).

Golden rule: **the live binary is the source of truth.** After you locate the
exe, `-cli help` and `-cli help <topic>` are served *by that binary*, so they
always match the installed command set. When unsure of a flag, ask the binary —
don't trust a memorized list (this one included).

---

## Mission 0 — Locate the exe (do this first, every session)

The exe is frequently **not on PATH**, and for local development you almost
always want the *Debug build* (internal test build) over an installed copy.
Resolve in this priority order:

1. **Debug build (internal test — PREFER THIS when developing):**
   `<repo>\Project\AgentZeroWpf\bin\Debug\net10.0-windows\AgentZeroLite.exe`
2. **Release build:**
   `<repo>\Project\AgentZeroWpf\bin\Release\net10.0-windows\AgentZeroLite.exe`
3. **Installed (Inno Setup default `{autopf}\AgentZeroLite`):**
   `%ProgramFiles%\AgentZeroLite\AgentZeroLite.exe`
4. **On PATH:** `Get-Command AgentZeroLite.exe`

Run the locator (PowerShell) — it returns the first hit by that priority and
verifies build identity:

```powershell
& .claude/skills/agentzero-cli/scripts/find-cli.ps1
```

Or inline, from the repo root:

```powershell
$c = @(
  "Project\AgentZeroWpf\bin\Debug\net10.0-windows\AgentZeroLite.exe",
  "Project\AgentZeroWpf\bin\Release\net10.0-windows\AgentZeroLite.exe",
  "$env:ProgramFiles\AgentZeroLite\AgentZeroLite.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $c) { $c = (Get-Command AgentZeroLite.exe -ErrorAction SilentlyContinue).Source }
$Exe = (Resolve-Path $c).Path
```

**Confirm which build answered** before you trust output — a stale PATH copy is
the #1 source of confusion. `version` needs no GUI:

```powershell
Start-Process $Exe -ArgumentList '-cli','version' -NoNewWindow -Wait
# → AgentZero Lite CLI <ver>  /  exe: <path>  /  base: <dir>
```

The `.ps1` wrapper (`AgentZeroLite.ps1`) ships **next to the exe** in every
build/install and prepends `-cli` for you. If it's present, prefer it — it
already does the `Start-Process` dance below.

---

## Invoking on Windows — why `Start-Process -Wait`

`AgentZeroLite.exe` is a **Windows-subsystem (GUI) binary**. When you call it
bare from a shell (`& $Exe -cli status`), the shell does *not* block and console
attachment races — you often get no output. Two reliable patterns:

**A. The wrapper (simplest — use when the `.ps1` sits beside the exe):**
```powershell
& "<dir>\AgentZeroLite.ps1" status
& "<dir>\AgentZeroLite.ps1" terminal-list
```

**B. `Start-Process -NoNewWindow -Wait` (works with the raw exe):**
```powershell
Start-Process $Exe -ArgumentList '-cli','terminal-list' -NoNewWindow -Wait
```

To **capture** output for parsing, redirect to a file (Start-Process can't pipe):
```powershell
Start-Process $Exe -ArgumentList '-cli','terminal-list' -NoNewWindow -Wait `
  -RedirectStandardOutput out.txt
$json = Get-Content out.txt -Raw
```

From **cmd.exe / a plain console** (not PowerShell), the exe attaches to the
parent console, so a direct call usually works:
```cmd
AgentZeroLite.exe -cli status
```
…but PowerShell + `Start-Process -Wait` is the recommended default because it
behaves the same whether launched interactively or from an agent, and it's what
`AgentZeroLite.ps1` does internally. **Default to PowerShell.**

Global options (all commands): `--no-wait` (fire-and-forget, skip the response
round-trip) and `--timeout <ms>` (default 5000). Output is UTF-8.

---

## The command surface (map)

Full, version-matched listing: `-cli help`. Grouped here by what they touch:

| Group | Commands | Needs GUI? |
|-------|----------|-----------|
| App | `status`, `open-win`, `close-win`, `console`, `copy`, `version`, `log` | version/open-win: no · rest: yes |
| **Terminals** | `terminal-list`, `terminal-read`, `terminal-send`, `terminal-key`, `terminal-wait` | **yes** |
| Agent state | `agent-state`, `agent-resume`, `agent-resume-cmd` | state/resume: yes · resume-cmd: no |
| Peer channel | `bot-chat "<text>" --from <name>` | yes |
| Worktrees | `worktree list\|add\|remove` | no (in-process git) |
| Multi-agent | `orchestrate list\|create\|status\|run` | run: yes · rest: no |
| Scheduling | `automation create\|list\|remove\|due` | no (GUI scheduler fires them) |
| Cost | `cost` | no (reads local DB) |
| **Native OS control** | `os list-windows\|screenshot\|element-tree\|activate\|mouse-*\|keypress\|…` | **no** |
| Integration | `agent-hook*`, `trust-workspace`, `skill-stub-*` | no |

Two important classes:
- **IPC commands** round-trip to the running GUI over `WM_COPYDATA` (marker
  `"AL"` / `0x414C`) and read a JSON reply from a named memory-mapped file.
  These need the GUI up; if it isn't, you get *"AgentZero Lite GUI is not
  running"* — start it with `open-win`.
- **In-process commands** (`os *`, `worktree`, `cost`, `automation`,
  `agent-hook-install`, …) run entirely inside the CLI invocation and work with
  **no GUI**.

Detail per command: **[references/command-reference.md](references/command-reference.md)**.

---

## Talking to terminals — the part `help` can't teach

Inside AgentZero, each workspace holds terminal *tabs*, addressed by
`<group_index> <tab_index>` (both zero-based). `terminal-list` prints the map
(and each tab's session id + HWND). From there you can **drive another agent's
terminal**:

```powershell
# 1. Discover indices
… -cli terminal-list

# 2. Type a command into tab [0:1] (text + Enter)
… -cli terminal-send 0 1 "git status"

# 3. Send a control key (no text) — e.g. interrupt a runaway TUI
… -cli terminal-key 0 1 ctrlc      # cr|lf|crlf|esc|tab|ctrlc|ctrld|up|down|…|hex:XX

# 4. Read its output back
… -cli terminal-read 0 1 --last 2000
```

### Don't poll — wait

Never loop `terminal-read` + sleep. Block on the terminal instead:

```powershell
# Wait until the tab's output stops changing (TUI settled)
… -cli terminal-wait 0 1 --idle-ms 1500 --timeout-ms 60000

# Or wait for a detected lifecycle STATE of a hosted agent CLI
… -cli terminal-wait 0 1 --until done --agent claude   # working|blocked|idle|done
```

`agent-state` gives a one-shot rollup of every tab's detected state plus an
"agents needing attention" count (blocked, or done-but-unseen).

### The handshake + reverse channel (DONE protocol)

Terminals are **half-duplex from the CLI's view**: you can *type into* a peer
(`terminal-send`) and *read* it, but for a peer agent to send a message **back
to the coordinator** (and to voice, if enabled), it must run a CLI command in
its own terminal:

```
AgentZeroLite.exe -cli bot-chat "DONE(your reply here)" --from <peerName>
```

`<peerName>` is the contract — it must match the peer's tab identity so the
AgentBot broker routes the reply into the active conversation instead of
dropping it as an "inactive peer signal". The broker tracks per-peer handshake
state: `NotConnected → HandshakeSent → Connected`. The **first** `bot-chat`
callback from a peer flips it to `Connected`.

**To open a discussion channel with a peer terminal, send the handshake first**
— a short message that teaches it the reverse channel, then wait for its `ready`
ack:

```powershell
$peer = "Claude"
$hs = @"
[handshake] Replies must be sent by running this in YOUR terminal:
    AgentZeroLite.exe -cli bot-chat "DONE(<text>)" --from $peer
Writing to screen only is NOT heard. Ack when ready:
    AgentZeroLite.exe -cli bot-chat "DONE(ready)" --from $peer
"@
… -cli terminal-send 0 1 $hs
… -cli terminal-wait 0 1 --until idle --timeout-ms 30000
… -cli terminal-read 0 1 --last 500      # confirm it ran the ready line
```

Keep every reply wrapped in `DONE(...)` — the broker unwraps it for display and
voice routing. Bare acks (`ready`, `ok`, `ack`) are recognized as handshake
acks and not treated as task answers.

### Mutual discussion / debate loop (터미널끼리 토론)

Two agent tabs can hold a back-and-forth. You (or a coordinator tab) relay:
send a turn into tab B, wait for it to settle, read its `DONE(...)`, feed that
into tab A, and repeat. The building blocks are exactly the four terminal verbs
plus the handshake. A ready-to-run relay/debate recipe is in
**[references/interaction-protocol.md](references/interaction-protocol.md)** —
read it before orchestrating any two-terminal conversation, because ordering
(handshake → wait → read → forward) and the `--from` naming are what make
messages actually land instead of being dropped.

---

## Native Windows control (`os` verbs)

The `os` group is AgentZero's built-in Windows automation surface and runs
**in-process — no GUI needed**. Read-only verbs are always available; input
simulation is gated.

```powershell
# Read-only (always allowed)
… -cli os list-windows --filter chrome
… -cli os get-window-info 0x00120A5C
… -cli os screenshot --hwnd 0x120A5C --color      # PNG → tmp/os-cli/screenshots/
… -cli os element-tree 0x120A5C --search Submit    # UI Automation tree
… -cli os text-capture 0x120A5C
… -cli os activate 0x120A5C                        # bring window to foreground
… -cli os dpi

# Input simulation — GATED. Needs --allow-input OR env AGENTZERO_OS_INPUT_ALLOWED=1
… -cli os mouse-click 640 400 --allow-input
… -cli os keypress "ctrl+shift+t" --allow-input    # 'alt+f4', 'f5', 'escape', …
… -cli os mouse-wheel 640 400 -120 --allow-input
```

HWNDs accept decimal or `0x`-hex. Every `os` verb prints JSON; input actions and
reads are appended to an audit log — inspect with `… -cli os audit --last 20`.
Full verb list: `… -cli os help`.

---

## Gotchas & etiquette

- **GUI-not-running** on an IPC command → run `… -cli open-win` (launches the
  GUI) and retry once it's up. `os *`, `worktree`, `cost`, `version` don't care.
- **Confirm the build.** If output looks wrong, run `version` and check the
  `exe:` line points at the build you intend (Debug for internal testing).
- **Unresponsive GUI:** IPC has a 3 s send timeout. If you see *"WM_COPYDATA
  timed out"*, the GUI is alive but its UI thread is stuck — check its log
  panel, don't hammer retries.
- **Fire-and-forget:** add `--no-wait` for `terminal-send`/`bot-chat`/`agent-hook`
  when you don't need the JSON reply (faster, non-blocking).
- **Input gate is deliberate.** Never sprinkle `--allow-input` reflexively —
  it moves the real mouse/keyboard. Prefer read-only `os` verbs and terminal
  IPC; use input simulation only when there's no API path.
- **Ask the binary, not your memory.** `-cli help` and `-cli help agentzero`
  are served live and version-matched. Re-run them if a command errors.
