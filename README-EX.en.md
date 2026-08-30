# AgentZero Lite — Extension Features Manual (README-EX)

> 🇰🇷 한국어: [README-EX.md](README-EX.md) · ⬅ Back to [README.md](README.md)

This document is a practical manual covering only the **recently added extension
features**. For the core product, see [`README.md`](README.md).

Extension features come in two forms:
- **CLI extensions** — script the app via `AgentZeroLite.exe -cli <command>`
- **GUI / bot extensions** — new in-app features (file tools, Diff Review, Ctrl+J command palette, agent state detection)

---

## 0. Setup

```powershell
# (once) build the latest
dotnet build Project/AgentZeroWpf/AgentZeroWpf.csproj -c Debug

# convenience: capture the exe path (PowerShell)
$AZ = "Project\AgentZeroWpf\bin\Debug\net10.0-windows\AgentZeroLite.exe"

# list every command
& $AZ -cli help
```

> Most CLI commands work **without the GUI running** (they touch files / DB / git
> directly). Only the terminal-facing commands (`terminal-*`, `agent-hook`) need a
> running GUI.

---

## 1. Bot Workspace File Tools 🗂️

Put the bot in **AI mode** and it can directly read / write / edit / search files
**inside your currently-active workspace folder**. Just ask in natural language.

| What you want | Ask the bot | Underlying tool |
|---|---|---|
| List files | "what files are in this folder?" | list_files |
| Read a file | "read README.md" | read_file |
| Summarize a file | "summarize Program.cs" | read_file |
| Search content | "find 'TODO' in this project" | grep |
| Edit a file | "change port to 8080 in config.json" | edit_file |
| New file | "create notes.md with the meeting minutes" | write_file |

**Important — scope (safety)**
- File operations are limited to the **currently selected workspace folder**.
  Switch workspaces and the target folder switches with it.
- Paths that escape the folder (`..`, absolute paths outside it) are rejected.
- With no workspace bound, file access is denied (default-deny).

**Accuracy tip**: give the exact filename incl. extension ("README.md", not
"README"). The bot can now **discover exact names itself via `list_files`** —
ask "see what files exist, then summarize the README" and it chains list → read.

**Verify (log)**: `logs\app-log.txt` records which folder was targeted, e.g.
`[AIMODE] read_file root="C:\...\your-workspace" path="README.md"`.

---

## 2. Diff Review & Re-dispatch to the Agent 🔍

Review your changes (a diff) inside the app, drop line comments, and **ship them
all to the agent as a single follow-up task**.

**How to use**
1. Click the **Diff Review icon** in the left ActivityBar.
2. The active workspace's git changes render with +/- coloring.
   - *If there are no changes you'll see "No changes"* — edit some code first.
3. Click **💬** on a line → type a comment → **Add**.
4. After commenting several lines, click **"Ship N to agent"** (top-right).
5. The collected comments are **bundled into one instruction** and sent to the bot AI.

Comments are persisted and survive across sessions.

---

## 3. Multi-Agent Collaboration 🤝

Open **two or more** coding-agent CLIs in terminals and they (or your scripts) can
instruct each other and read back results.

```powershell
& $AZ -cli terminal-list                        # find group/tab indices
& $AZ -cli terminal-send 0 1 "write tests for this file"  # instruct agent in (group0, tab1)
& $AZ -cli terminal-wait 0 1 --idle-ms 2000      # wait until it goes idle (done)
& $AZ -cli terminal-read 0 1 --last 3000         # read the result
```

- **`terminal-wait`** is the key. Instead of a sleep-poll loop, it waits precisely
  until the target terminal's output stops changing (a "done" signal) → one agent
  can **supervise** another.
- Typical uses: **review pair** (A writes → delegate review to B → apply feedback),
  **split work** (A = implementation, B = tests), **moderated debate** (bot relays
  between the two).

> To let the agents **learn these commands themselves**, install the "guide stub"
> in section 6.

---

## 4. git Worktree Parallel Work 🌿

Work the same repository on multiple branches **without conflicts** — great for
giving each agent an isolated checkout.

```powershell
& $AZ -cli worktree list                         # current worktrees
& $AZ -cli worktree add ..\repo-featB featB       # new isolated folder on branch featB
& $AZ -cli worktree add ..\repo-x --trust         # create + pre-trust for agent CLIs (see 6)
& $AZ -cli worktree remove ..\repo-featB           # clean up
```

Agent A works on main, Agent B works in the `featB` worktree → merge later.

---

## 5. Supervised Runs (Orchestration) 🧭

Bundle multiple tasks with **dependencies (a DAG)** into a managed run where a
**coordinator automatically dispatches and supervises the tasks across your
running terminal agents**.

```powershell
# task definition file (deps declare dependencies)
#   run.json
#   { "name":"build-pipeline",
#     "tasks":[ {"key":"a","prompt":"generate code","deps":[]},
#               {"key":"b","prompt":"run tests","deps":["a"]} ] }

& $AZ -cli orchestrate create run.json    # create a plan → "Created run #N"
& $AZ -cli orchestrate status N            # tasks / deps / status (b ← [a])
& $AZ -cli orchestrate run N               # run it! ready tasks go to terminal agents
& $AZ -cli orchestrate list                # recent runs
```

**How it works**: on `run`, each running terminal becomes a worker. The
coordinator sends dependency-satisfied tasks to workers (types the prompt into
the terminal), and advances to the next task once that terminal goes **idle
(done)**. When all tasks finish, the run is saved as done.

> `run` needs the GUI + live terminal agents. Track progress with
> `orchestrate status N`. (Single delegation also works via §3 multi-agent collaboration.)

---

## 6. Agent Integration Installers (optional · consent required) 🔗

Installable features that integrate more smoothly with agent CLIs (Claude Code /
Cursor / Copilot / Codex). These **modify files in your home directory**, so install
them only when you want, explicitly. Uninstallers are provided.

| Feature | What it does | Install | Uninstall |
|---|---|---|---|
| **Status hooks** | Report the agent's real state to the bot — **Claude + Codex/Cursor** (only installed CLIs) | `-cli agent-hook-install` | `-cli agent-hook-uninstall` |
| **Folder trust** | Pre-trust a folder so the "trust this folder?" prompt won't swallow injected input | `-cli trust-workspace [path]` | delete the trust files manually |
| **Guide stub** | Let agents learn the CLI above by themselves (`-cli help`) | `-cli skill-stub-install` | `-cli skill-stub-uninstall` |

```powershell
& $AZ -cli agent-hook-install     # register status hooks in ~/.claude*/settings.json + ~/.codex, ~/.cursor (backed up)
& $AZ -cli trust-workspace .      # trust the current folder for each agent CLI
& $AZ -cli skill-stub-install     # inject a usage stub into the agent's skills folder
```

- These commands modify files **only when run directly** (no automatic/implicit install).
- Hooks/stubs are marker-identified, so uninstall **leaves your other settings untouched.**

---

## 7. Token Cost Estimate 💰

Estimates cost per model from recorded token usage.

```powershell
& $AZ -cli cost
# e.g.
# Estimated cost (all recorded turns): $XX.XX  (N turns)
# By model:
#   claude-opus-...      $ ...   (N turns)
```

> Prices are **editable default estimates**, not a live feed.

### 💲 Budget tab (GUI)

Settings → **💲 Budget** turns the same telemetry into a budget view:

- **Monthly cap (USD)** — set a spend ceiling; `0` = no cap.
- **Editable price table** — a grid of `Key / Input / Output / Cache+ / Cache-`
  (USD per 1M tokens). Matched by lower-cased substring, first match wins, tried
  before the built-in defaults. Empty table = defaults.
- **Month-to-date spend readout** — computed from `TokenUsageRecords` since the
  start of the current month using your effective price table. It turns **amber**
  at ≥80 % of the cap and **red** once over.

Overrides persist to `%LocalAppData%\AgentZeroLite\budget-settings.json`.

---

## 8. Built-in Guide Serving 📖

Agents/users can look up the current usage anytime (always matches the running build).

```powershell
& $AZ -cli help agentzero      # agent-control guide (terminals / worktrees / cost)
& $AZ -cli help orchestrate    # orchestration guide
```

---

## 9. Scheduled Automations ⏰

Run a prompt into the bot **on a recurring schedule** — daily reviews, periodic
summaries, and the like.

```powershell
# every 30 minutes / top of every hour / daily at a UTC time
& $AZ -cli automation create --name "daily-review" --schedule "daily 09:00" --prompt "summarize today's changes"
& $AZ -cli automation create --name "ping" --schedule "every 30m" --prompt "check the build status"
& $AZ -cli automation list       # registered automations + next run time
& $AZ -cli automation due         # what's due right now
& $AZ -cli automation remove 3    # delete
```

- Schedule forms: **`every <N>m`** / **`every <N>h`** / **`hourly`** / **`daily HH:mm`** (UTC).
- While the GUI is running, the scheduler (60 s tick) fires due automations into
  the bot AI and advances their next-run time.

---

## 10. Command Palette (Ctrl+J) 🎯

Press **Ctrl+J** to **fuzzy-jump** to any workspace or command from anywhere.

1. `Ctrl+J` → a search box appears
2. Type a fragment — e.g. `dr` → **Diff Review**, `web` → **WebDev**, or part of
   a workspace name to switch to it
3. `↑`/`↓` to move, `Enter` to run, `Esc` to close

Targets: open **workspaces** (switch to) + key **commands** (Diff Review / Bot /
Harness / WebDev / Scrap / Note). Keyboard-first navigation instead of hunting the
ActivityBar with the mouse.

---

## 11. Agent State Detection 🚦

Detects the **live state** of your hosted coding agents (Claude/Codex/…) so you can
see at a glance **which of many agents is waiting on you**.

- **SESSIONS state chip** — each session row shows a colored chip:
  🔴 `blocked` (waiting on approval/input) · 🟡 `working` (generating) · 🔵 `done`
  (finished but not yet seen) · ⚪ `idle`. Blocked / unseen-done are bolded.
- **Title bar** — `AgentZero Lite ● N need attention`.
- **Taskbar flash** — when an agent newly becomes blocked/done, the taskbar flashes
  (only while the window is in the background).

```powershell
& $AZ -cli agent-state
#  Agents needing attention: 1
#    [5:0] blocked  * Claude ←     ← waiting on approval (* = unseen)
```

**How it works**: covers CLIs without hooks too. It reads the terminal screen against
rules (manifests) to classify state. Rules are **data you can tune** — drop
`%LOCALAPPDATA%\AgentZeroLite\agent-detection\<agent>.json` to override the built-ins
(claude/codex/generic) without a rebuild.

**Wait for a state** (for scripts/agents):
```powershell
& $AZ -cli terminal-wait 0 1 --until blocked --agent claude   # until it waits on approval
& $AZ -cli terminal-wait 0 1 --until idle                     # until it finishes
```

**Restore a conversation** — find a folder's latest Claude session and print the resume command:
```powershell
& $AZ -cli agent-resume-cmd "C:\code\myproj"   # by folder
& $AZ -cli agent-resume 5 0                     # auto-resolve from a tab's (group 5, tab 0) workspace
#  claude --resume 8151ecda-83b1-450d-...
```
> `agent-resume` / `agent-resume-cmd` only **print** the command (safety). Run it
> yourself when ready — `--resume` restores the same conversation.

**Or inject it automatically** — `agent-resume-launch` does the same discovery but
`WriteAndSubmit`s the resume command straight into the live terminal:
```powershell
& $AZ -cli agent-resume-launch 5 0             # inject into tab [5:0]
& $AZ -cli agent-resume-launch --alias build   # …or target it by alias
```

---

## CLI Command Summary

| Command | Description | Needs GUI |
|---|---|---|
| `cost` | token usage → cost estimate | ✕ |
| `worktree <list\|add\|remove>` | manage git worktrees (`add ... --trust`) | ✕ |
| `orchestrate <list\|create\|status>` | create/inspect supervised runs | ✕ |
| `orchestrate run <id>` | run — dispatch/supervise across terminal agents | ○ |
| `automation <create\|list\|remove\|due>` | scheduled runs (every/hourly/daily) | ✕ |
| `agent-state` | detected state per terminal + attention rollup | ○ |
| `terminal-wait <g> <t> --until <state>` | wait until a state (working/blocked/idle/done) | ○ |
| `agent-resume-cmd [cwd]` | print resume command for a folder's latest Claude session | ✕ |
| `agent-resume <g> <t>` | auto-discover a tab's workspace session → resume command | ○ |
| `agent-resume-launch <g> <t>` | inject the resume command into the live terminal (also `--alias`) | ○ |
| `help [topic]` | serve guides (agentzero/orchestrate) | ✕ |
| `trust-workspace [path]` | trust a folder for agent CLIs | ✕ |
| `agent-hook-install` / `-uninstall` | install/remove status hooks | ✕ |
| `skill-stub-install` / `-uninstall` | install/remove the usage stub | ✕ |
| `terminal-list` | list terminal groups/tabs | ○ |
| `terminal-read <g> <t> [--last N]` | read terminal output | ○ |
| `terminal-send <g> <t> "<text>"` | send input to a terminal (also `--alias <name>`) | ○ |
| `terminal-alias <list\|set <g> <t> <name>\|rm <name>>` | name a terminal so send/key/read can target it by `--alias` | ○ |
| `terminal-wait <g> <t> [--idle-ms N]` | wait until a terminal is idle (done) | ○ |

---

## Verify / Troubleshoot

- **Automated E2E**: `pwsh Test/e2e/run-all.ps1` (CLI+GUI) / `-SkipGui` (CLI only).
  A healthy build prints `E2E PASSED`. Results/screenshots land in `Test/e2e/_artifacts/`.
- **Logs**: `Project\AgentZeroWpf\bin\Debug\net10.0-windows\logs\app-log.txt`
  - File tools: `[AIMODE] read_file/grep root="..." path="..."`
  - Diff Review: `[DiffReview]`
- **"file not found"**: check the filename extension, and that the **active workspace**
  is the folder you meant (see `root="..."` in the log).
- **Terminal commands do nothing**: make sure the GUI is running. New features require
  **restarting** the app to take effect.
