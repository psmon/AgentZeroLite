# Terminal Wedge Triage — "this CLI tab is frozen"

> Owner: **`code-coach`** (primary) — playbook for diagnosing the recurring
> "터미널 CLI 창 N개 중 하나 프리징" report.
> Cross-reference: **`tamer`** (secondary) — when the wedge is recurring on a
> specific child command, the gardener may need to file a follow-up mission.

A "frozen tab" inside the GUI's terminal area is a **separate** failure class
from the WM_COPYDATA infinite-block in [`wm-copydata-ipc-pitfalls.md`](wm-copydata-ipc-pitfalls.md).
WM_COPYDATA is GUI ↔ external CLI process; this is *inside* the GUI — a child
process behind ConPTY stops responding to stdin while sibling tabs are healthy.
The triage paths share no code.

`ConPtyTerminalSession` ships a built-in health state machine (since `0bc0b35`)
and surfaces a wedge-recovery banner. The job here is to read its log output
and decide who's stuck and why.

---

## Health state machine recap

`Project/AgentZeroWpf/Services/ConPtyTerminalSession.cs`:

- Every input attempt (Write, SendControl, keyboard) schedules a 1 s
  output-growth check (`EchoCheckMs = 1000`).
- No growth → `_consecutiveNoEcho++`.
- Output growth (detected by the poll thread) atomically resets the counter
  and restores `Alive`.
- Thresholds: `Stale` at 3 consecutive no-echo, `Dead` at 5.
- On `Dead`, the UI fires the wedge banner with a "Restart Terminal" action.

The thresholds are intentionally conservative — slow TUI loads (claude CLI
startup, tailscale ssh handshake) can briefly produce no echo without being
truly stuck.

---

## Triage procedure

### Step 1 — `terminal-list` (snapshot of all tabs)

```bash
Project/AgentZeroWpf/bin/Debug/net10.0-windows/AgentZeroLite.exe -cli terminal-list
```

The output reports `running: true/false` per tab but **not** `HealthState`
(today's IPC payload doesn't carry it). Use this only to confirm which tabs
exist and their session IDs. The frozen tab will still report `running: true`
because the PTY pair and child process are alive — only the input loop is
stuck.

> Future improvement: extend the IPC payload to expose
> `ConPtyTerminalSession.HealthState`. Mission ticket worth filing if this
> triage happens > 2x/month.

### Step 2 — read the app log for HEALTH transitions

The live GUI's app log:

```
Project/AgentZeroWpf/bin/<config>/net10.0-windows/logs/app-log.txt
```

Three event families to grep — present them in order, not separately:

```bash
# (use the Grep tool with output_mode=content)
pattern: HEALTH|INPUT-NO-ECHO|Wedge banner|Session created|control rejected
```

Decoding:

| Log line                                       | Means                                                     |
|------------------------------------------------|-----------------------------------------------------------|
| `[Session] INPUT-NO-ECHO \| id=… source=…`     | Single 1 s check found no output growth after that input  |
| `[Session] HEALTH \| state=Stale consecutive=3`| Early warning — tab is stalling                           |
| `[Session] HEALTH \| state=Dead consecutive=5` | **Wedged** — the UI banner will fire next                 |
| `[CLI] Wedge banner shown \| label=…`          | UI surfaced the recovery affordance                       |
| `[Session] HEALTH \| state=Alive (output recovered)` | Self-healed — false alarm                          |

The `id` and `label` fields identify the exact session — match them back to
the `terminal-list` output.

### Step 3 — confirm the wedge is real (not just slow output)

A truly wedged tab shows `outLenStable=N` repeated across multiple
`INPUT-NO-ECHO` lines with the **same N**. If `N` is changing — even slowly —
the child is producing output and you're seeing keystroke buffering, not a
wedge. Don't restart in that case.

Example of a real wedge (from `2026-05-10` triage):

```
[08:05:32.941] INPUT-NO-ECHO  source=keyboard:S  outLenStable=3089
[08:05:33.034] INPUT-NO-ECHO  source=keyboard:S  outLenStable=3089
[08:05:33.034] HEALTH         state=Dead consecutive=5
[08:05:33.092] Wedge banner shown   label=NotePC-32GB
```

`outLen` is pinned at 3089 across multiple checks — the child is not producing
output either, so it's not "thinking and consuming input silently", it's
genuinely stuck.

### Step 4 — attribute the wedge to the child command

Find the most recent `RestartTerm()` or `ConPTY 터미널 생성` line for the same
session id. The command string after `cmd /c "set "PATH=…&&pushd "<dir>"&&<cmd>"`
tells you what the wedge is *of* — not the harness:

| Child command                                    | Most-likely wedge cause                                                                     |
|--------------------------------------------------|---------------------------------------------------------------------------------------------|
| `pwsh.exe -NoExit -Command claude`               | Anthropic Claude CLI Node TUI lost stdin loop (slow API, mid-stream cut, raw-mode glitch)   |
| `pwsh.exe -NoExit -Command codex`                | Codex CLI similar; trailing `\r` swallow has its own mitigation in `WriteAndEnter`          |
| `pwsh.exe -NoExit -Command ssh user@host`        | Remote sshd pause / network blip / sudo password TTY mode confusion                         |
| `pwsh.exe`                                       | PSReadLine in the wrong mode after a paste, foreign-language IME, or `Read-Host` prompt     |
| `cmd.exe`                                        | Rare; usually a child of `cmd` (e.g. `pause`) with input redirection                        |

If the wedge is in a child command we own (a wrapper script, a future
plugin), open a bug. If the wedge is in a third-party CLI (claude, ssh,
codex), we cannot fix the child — restart-tab is the standard recovery.

### Step 5 — recovery

- **Operator path**: click "Restart Terminal" in the wedge banner. Fires
  `RestartTerm()`, which keeps the tab/session id but re-creates the PTY pair.
- **CLI path**: there is no `terminal-restart` verb today. If the harness
  needs to drive recovery from outside the GUI, file a feature ticket — until
  then, wedge recovery is operator-driven.

---

## Sibling-tab differential — when "the system" looks frozen

The first reflex when a tab freezes is to suspect IPC, ConPTY, or Akka. Almost
always wrong. Use the sibling tabs:

- If **other tabs in the same workspace** still echo input → ConPTY/actor/IPC
  layer is fine. Wedge is local to one child.
- If **all tabs in the same workspace** stopped echoing → look at the workspace
  actor (`/user/stage/ws-<name>`) and the dispatcher.
- If **all tabs across all workspaces** stopped echoing → suspect WM_COPYDATA
  P1 (the GUI UI thread is blocked) or a global Akka pause, not per-session
  health.

Confirming the differential takes one Bash invocation against the live log;
do it before proposing structural fixes.

---

## Recurring-wedge follow-up

If the same `label` (e.g. `AgentTest/NotePC-32GB`) goes Dead more than once
in a session, or the same child command (e.g. `pwsh -NoExit -Command claude`)
wedges across multiple labels, that's a pattern worth recording:

1. Append a one-line entry to `harness/knowledge/_shared/cases/` (or create
   one) with the session id, child command, and the `outLenStable` value at
   wedge time.
2. If it recurs ≥ 3 times in a week, file a tracking issue — even if we can't
   fix the third-party child, the cumulative log proves the dependency is
   flaky and informs future scheduling decisions (which CLI to prefer for
   long-running interactive sessions).

## Cross-reference

- IPC-class freezes (different): `harness/knowledge/code-coach/wm-copydata-ipc-pitfalls.md`
- Health state machine: `Project/AgentZeroWpf/Services/ConPtyTerminalSession.cs`
  (search for `EchoCheckMs`, `StaleThreshold`, `DeadThreshold`)
- Wedge banner UX: commit `0bc0b35 feat(terminal): wedge-recovery UX`
- Live diagnosis worked example: this triage on `2026-05-10` correctly
  attributed `NotePC-32GB` freeze to claude CLI while sibling ssh tabs
  remained Alive.
