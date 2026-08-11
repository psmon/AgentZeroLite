# AgentZero Lite — Extension Features Manual (README-EX)

> 🇰🇷 한국어: [README-EX.md](README-EX.md) · ⬅ Back to [README.md](README.md)

This document is a practical manual covering only the **recently added extension
features**. For the core product, see [`README.md`](README.md).

Extension features come in two forms:
- **CLI extensions** — script the app via `AgentZeroLite.exe -cli <command>`
- **GUI / bot extensions** — new in-app features (file tools, Diff Review)

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
"README"). If unsure, first "find 'class' in this folder" (grep) to locate it.

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

Bundle multiple tasks with **dependencies (a DAG)** into a managed run. Currently
this provides **saving and inspecting** the plan.

```powershell
# task definition file (deps declare dependencies)
#   run.json
#   { "name":"build-pipeline",
#     "tasks":[ {"key":"a","prompt":"generate code","deps":[]},
#               {"key":"b","prompt":"run tests","deps":["a"]} ] }

& $AZ -cli orchestrate create run.json    # create a run → "Created run #N"
& $AZ -cli orchestrate status N            # tasks / deps / status (b ← [a])
& $AZ -cli orchestrate list                # recent runs
```

> Automatically dispatching / supervising each task on live terminal agents is a
> follow-up. For now, dispatch manually as in section 3 (multi-agent collaboration).

---

## 6. Agent Integration Installers (optional · consent required) 🔗

Installable features that integrate more smoothly with agent CLIs (Claude Code /
Cursor / Copilot / Codex). These **modify files in your home directory**, so install
them only when you want, explicitly. Uninstallers are provided.

| Feature | What it does | Install | Uninstall |
|---|---|---|---|
| **Status hooks** | Report the agent's real state to the bot (replaces terminal scraping) | `-cli agent-hook-install` | `-cli agent-hook-uninstall` |
| **Folder trust** | Pre-trust a folder so the "trust this folder?" prompt won't swallow injected input | `-cli trust-workspace [path]` | delete the trust files manually |
| **Guide stub** | Let agents learn the CLI above by themselves (`-cli help`) | `-cli skill-stub-install` | `-cli skill-stub-uninstall` |

```powershell
& $AZ -cli agent-hook-install     # register status hooks in ~/.claude*/settings.json (backed up)
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

---

## 8. Built-in Guide Serving 📖

Agents/users can look up the current usage anytime (always matches the running build).

```powershell
& $AZ -cli help agentzero      # agent-control guide (terminals / worktrees / cost)
& $AZ -cli help orchestrate    # orchestration guide
```

---

## CLI Command Summary

| Command | Description | Needs GUI |
|---|---|---|
| `cost` | token usage → cost estimate | ✕ |
| `worktree <list\|add\|remove>` | manage git worktrees (`add ... --trust`) | ✕ |
| `orchestrate <list\|create\|status>` | create/inspect supervised runs | ✕ |
| `help [topic]` | serve guides (agentzero/orchestrate) | ✕ |
| `trust-workspace [path]` | trust a folder for agent CLIs | ✕ |
| `agent-hook-install` / `-uninstall` | install/remove status hooks | ✕ |
| `skill-stub-install` / `-uninstall` | install/remove the usage stub | ✕ |
| `terminal-list` | list terminal groups/tabs | ○ |
| `terminal-read <g> <t> [--last N]` | read terminal output | ○ |
| `terminal-send <g> <t> "<text>"` | send input to a terminal | ○ |
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
