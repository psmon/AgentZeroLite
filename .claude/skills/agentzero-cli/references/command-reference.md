# AgentZero Lite CLI — command reference

Authoritative source is the live binary: `AgentZeroLite.exe -cli help` and
`-cli help <topic>` (topics include `agentzero`, `orchestrate`). This file is a
navigation aid; when a flag disagrees, trust the binary.

All examples assume `$Exe` resolved via `find-cli.ps1` and invocation through
`Start-Process $Exe -ArgumentList '-cli',… -NoNewWindow -Wait` (or the
`AgentZeroLite.ps1` wrapper). `<G>`/`<T>` = group/tab index (zero-based).

## Global options

| Option | Effect |
|--------|--------|
| `--no-wait` | Send command, skip the memory-mapped-file response wait. Fire-and-forget. |
| `--timeout <ms>` | Max wait for the IPC reply. Default 5000. |

## App / window

| Command | GUI? | Notes |
|---------|------|-------|
| `status` | yes | Status bar text + workspace count. |
| `open-win` | no | Launch the GUI (or foreground it if already running). |
| `close-win` | yes | Post `WM_CLOSE` to the GUI. |
| `console` | no | Open PowerShell in the app directory. |
| `copy` | yes | Copy captured text to the clipboard. |
| `version` / `-v` | no | Build identity: version, exe path, base dir. Use to confirm which build answers. |
| `log [--last N] [--clear]` | no | CLI action history. |

## Terminals (IPC — GUI required)

| Command | Notes |
|---------|-------|
| `terminal-list` | Groups → tabs, each with `tab_index`, `title`, `active`, `running`, `session_id`, `hwnd`. Also dumps raw JSON. |
| `terminal-send <G> <T> <text…>` | Types text **+ Enter** into the tab. Accepts `--alias <name>` in place of `<G> <T>`. |
| `terminal-key <G> <T> <key>` | Raw key, no text. Keys: `cr lf crlf esc tab backspace del ctrlc ctrld up down left right hex:XX`. Accepts `--alias <name>`. |
| `terminal-read <G> <T> [--last N]` | Console output text; `--last N` = last N chars (default all). Accepts `--alias <name>`. |
| `terminal-wait <G> <T> [flags]` | Block until settled/state. See below. |
| `terminal-alias <list\|set <G> <T> <name>\|rm <name>>` | Stable name → terminal. `set` names the tab; then any send/key/read can target it with `--alias <name>` instead of volatile indices. Aliases persist to `terminal-aliases.json`. |

`terminal-wait` flags:
- `--idle-ms N` (default 1500) — consider settled after output is unchanged this long.
- `--timeout-ms N` (default 60000) — overall cap; exit code 2 on timeout.
- `--until <working\|blocked\|idle\|done>` — wait for a **detected agent lifecycle state** (not just idle). Pair with `--agent <name>` to pick the detection manifest.
- `--stall-ms N` (default 8000) — with `--until`, give up (exit 3) if the detected state stops changing before the target.

Exit codes: `0` reached/idle · `2` timeout · `3` stalled.

## Agent state / resume

| Command | GUI? | Notes |
|---------|------|-------|
| `agent-state` | yes | Per-tab detected state + "needs attention" rollup (blocked / done-unseen). |
| `agent-resume <G> <T>` | yes | Prints `claude --resume <id>` for that tab's workspace. Does **not** auto-restart. |
| `agent-resume-launch <G> <T>` | yes | Same discovery, but **injects** the resume command into the live terminal (`WriteAndSubmit`) instead of only printing it. Accepts `--alias <name>`. |
| `agent-resume-cmd [cwd]` | no | Same as agent-resume, for an arbitrary folder (default CWD). |

## Peer channel (IPC — GUI required)

| Command | Notes |
|---------|-------|
| `bot-chat "<text>" [--from <name>]` | Deliver a message to the AgentBot broker. From a peer terminal, wrap replies as `DONE(<text>)` and set `--from <peerName>` matching the tab identity, or the broker drops it as an inactive-peer signal. `--from` default is `CLI`. |

## Worktrees (in-process git — no GUI)

| Command | Notes |
|---------|-------|
| `worktree list` | List worktrees in the current repo. |
| `worktree add <path> [branch] [--trust]` | Create isolated checkout. `--trust` pre-marks the folder trusted for hosted agent CLIs so their trust prompt won't eat injected keystrokes. |
| `worktree remove <path> [--force]` | Remove a worktree. |

## Orchestration (supervised multi-agent DAG)

| Command | GUI? | Notes |
|---------|------|-------|
| `orchestrate list` | no | Last 20 runs. |
| `orchestrate create <file.json>` | no | Create a run. JSON: `{ "name": "...", "tasks": [ { "key","prompt","deps":[] } ] }`. |
| `orchestrate status <runId>` | no | Task states + dependency arrows. |
| `orchestrate run <runId>` | yes | Hands off to the GUI coordinator to dispatch live agents. Workers signal done with `bot-chat "DONE(...)" --from <worker>`. |

## Automation (scheduled runs — no GUI to create; GUI scheduler fires)

| Command | Notes |
|---------|-------|
| `automation create --schedule "<S>" --prompt "<P>" [--name N] [--workspace path]` | Schedule forms: `every 30m`, `hourly`, `daily HH:mm`. Computes next fire time. |
| `automation list` | All automations with next-run UTC. |
| `automation due` | What's due right now. |
| `automation remove <id>` | Delete one. |

## Cost (no GUI — reads local telemetry DB)

| Command | Notes |
|---------|-------|
| `cost` | Estimated USD from recorded token usage, total + per-model. Prices are editable defaults, not a live feed. |

## Native OS control (`os …` — in-process, no GUI)

Read-only verbs always available; input verbs gated by `--allow-input` or env
`AGENTZERO_OS_INPUT_ALLOWED=1`. All return JSON. HWND = decimal or `0x`-hex.

| Verb | Notes |
|------|-------|
| `os list-windows [--filter S] [--include-hidden]` | Enumerate top-level windows. |
| `os get-window-info <hwnd>` | Detail one window. |
| `os screenshot [--hwnd N] [--color] [--full]` | PNG → `tmp/os-cli/screenshots/`. Default grayscale + full desktop. |
| `os element-tree <hwnd> [--depth N] [--search S]` | UI Automation tree (depth default 30, clamp 1–100). |
| `os text-capture <hwnd>` | Text from a window's UI tree. |
| `os activate <hwnd>` | Foreground the window. |
| `os dpi` | System + monitor DPI report. |
| `os mouse-click <x> <y> [--right] [--double] [--allow-input]` | Gated. |
| `os mouse-move <x> <y> [--allow-input]` | Gated. |
| `os mouse-wheel <x> <y> <delta> [--allow-input]` | Gated. |
| `os keypress <spec> [--allow-input]` | Gated. `'ctrl+c'`, `'alt+f4'`, `'f5'`, `'escape'`, `'ctrl+shift+t'`. |
| `os audit [--last N]` | Today's OS-control audit log. |

## Integration / installer helpers (in-process, modify user profiles — consented)

| Command | Notes |
|---------|-------|
| `agent-hook-install` / `agent-hook-uninstall` | Add/remove state-reporting hooks in `~/.claude*/settings.json` (also Codex/Cursor). |
| `agent-hook --event <e> [--state s] [--session id] [--detail t]` | Fire a state report (fire-and-forget). Installed hooks call this. |
| `trust-workspace [path]` | Pre-trust a folder for Cursor/Copilot/Codex CLIs (default CWD). |
| `skill-stub-install` / `skill-stub-uninstall` | Inject/remove the tiny anti-drift AgentZero discovery stub into `~/.claude*` skills. |
