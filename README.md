# AgentZero Lite

**A minimalist IDE for the AI era — driving many CLIs side by side, from a single window.**

> 🇰🇷 한국어 문서: [README-KR.md](README-KR.md)

![AgentZero Lite — multi-CLI multi-view](Home/main.png)

🎬 **Demo** — driving Claude and Codex in parallel:

<a href="https://www.youtube.com/watch?v=Bpj8n29W6wY"><img src="https://img.youtube.com/vi/Bpj8n29W6wY/mqdefault.jpg" width="240" alt="Watch on YouTube"></a>

Pipe a single instruction to an AI CLI (Claude, Codex, any model you can run in
a shell) living in the same workspace — or in a different one — and have it act.
Run two different AI models side by side and let them talk to each other through
the same mechanism: cross-model dialogue, no custom broker required.


AgentZero Lite is a Windows desktop shell built around a simple idea: in the AI era most
of your day is spent *talking to command-line tools*. `claude`, `codex`, `gh`, `docker`,
`pwsh`, a REPL, a build log tail — each wants its own terminal, and you want all of them
visible at once without juggling windows. AgentZero Lite gives you a true multi-tab,
multi-workspace ConPTY terminal and a small chat surface that forwards text and skill
macros to whichever terminal is in focus — nothing more, nothing less.

---

## Features

- **Multi-tab ConPTY terminals** — real `conhost` rendering per tab, not a pseudo-PTY
  pretending. Powered by `EasyWindowsTerminalControl` / `CI.Microsoft.Terminal.Wpf`.
- **Workspaces** — group tabs by folder so each project keeps its own set of CLIs
  (one click = `cd` context and a fresh Claude).
- **AgentChatBot** — a dockable chat pane that forwards whatever you type into the
  **active** terminal. `CHT` mode types text, `KEY` mode forwards raw keystrokes
  (Ctrl+C, arrows, Tab). It is **not** an AI; it is an input broker.
- **AI ↔ AI conversation (the headline trick)** — teach `AgentZeroLite.ps1` to a
  Claude tab or a Codex tab *once* ("learn `AgentZeroLite.ps1 help` and use it for
  cross-terminal talk"), and from that point on either AI can greet the other
  terminal *by name* and strike up a real dialogue. Claude in tab 0 writes to Codex
  in tab 1, Codex replies back, each reads the peer's last output with
  `terminal-read`. No extra broker, no cloud relay — just the two CLIs poking each
  other through AgentZero's IPC. This is the tiki-taka between models that the Lite
  edition exists for.
- **AgentBot `[+]` menu — 3 ways to arm a terminal AI** —
  - **`AgentZeroCLI Helper`** — drops a ready-made briefing into the chat input that
    teaches any terminal AI (Claude, Codex, shell-hosted model) how to call
    `AgentZeroLite.exe -cli` once, no skill install. Review, hit Send, done. If the
    CLI is not on PATH the menu nudges you to *Settings → Register PATH* and
    restart first.
  - **`Import Starter Skills`** — copies the shipped `agent-zero-lite` skill into
    the active workspace's `.claude/skills/` so Claude Code picks it up persistently
    on next session.
  - **`Skill Sync`** — with Claude already running in a tab, reads the skill list
    out of its own `/skills` view and turns it into a slash-command menu in the
    chat box. Type `/`, pick a skill, Enter — the macro text is fired at the
    terminal. No LLM round-trip.
- **Notes with live rendering** — a second bottom panel with a Markdown viewer that
  also renders Mermaid diagrams and Pencil files, scoped to the active workspace
  folder.
- **CLI remote-control** — run `AgentZeroLite.exe -cli terminal-send 0 0 "npm test"`
  from any script and drive the GUI over `WM_COPYDATA` + memory-mapped files.
- **Actor model (Akka.NET)** — terminal lifecycle, workspace routing and chat input
  all run through supervised actors, so a crashing session does not take the window
  down with it.
- **One executable, one process** — single-instance guard, SQLite for config, zero
  external dependencies beyond the .NET 10 runtime. The build is under ~60 MB.

---

## Screenshot of the mental model

```
+--------------------------------------------------------------------------+
| AgentZero                                                    -  □  ×    |
+---+------------+-----------------------------------------------+--------+
|   | WORKSPACES | [Claude1] [pwsh1] [build-log] [+]            |        |
| ⚙ | ▸ monorepo +-----------------------------------------------+        |
| 🤖 |   ▸ web    |                                              |        |
|   |   ▸ api    |           ConPTY terminal (active tab)        |        |
|   | ▸ blog     |                                              |        |
|   |            |                                              |        |
|   | SESSIONS   +-----------------------------------------------+        |
|   |  · Claude1 | AGENT BOT ▾ | OUTPUT | LOG | NOTE                    |
|   |  · pwsh1   +-----------------------------------------------+        |
|   |            |  > /skills                                    |        |
|   |            |  [skill list]                                 |        |
|   |            |  > run tests and summarize                     [Send]  |
+---+------------+-----------------------------------------------+--------+
```

Top bar: ConPTY terminals, one per tab. Left rail: activity icons + sidebar with
workspaces and sessions. Bottom panel: tabbed — AGENT BOT (text/key sender to the
active terminal), OUTPUT, LOG, NOTE (per-workspace markdown viewer).

---

## Architecture

```
┌─ AgentZeroWpf (WinExe, WPF, net10.0-windows) ───────────────────────────┐
│                                                                         │
│  MainWindow  ──── hosts N ConPTY tabs  ──── AgentBotWindow (dock/float) │
│      │                                              │                   │
│      │  WM_COPYDATA + MMF  <─  CliHandler.cs  ──>   │                   │
│      │  (external scripts drive the GUI)            │                   │
│      ▼                                              ▼                   │
│  ActorSystemManager (Akka.NET)                                          │
└──────────────────────┬──────────────────────────────────────────────────┘
                       │  ProjectReference
┌─ ZeroCommon (ClassLib, net10.0) ────────────────────────────────────────┐
│  Actors/    Stage → Workspace(N) → Terminal(N)  + AgentBot (1)          │
│  Services/  ITerminalSession, AgentEventStream, AppLogger               │
│  Data/      AppDbContext + EF Core (SQLite)                             │
│             CliDefinition / CliGroup / CliTab / ClipboardEntry          │
│  Module/    CliTerminalIpcHelper, CliWorkspacePersistence, ...          │
└─────────────────────────────────────────────────────────────────────────┘
```

`ZeroCommon` is UI-free and covered by its own headless test project
(`ZeroCommon.Tests`, xUnit + Akka.TestKit). `AgentTest` covers the WPF-dependent
surface.

### Actor topology

```
/user/stage                  — supervisor, lifecycle broker, one per app
    /bot                     — AgentBotActor: mode (Chat/Key), UI callback
    /ws-<workspace>          — WorkspaceActor: owns terminals in a folder
        /term-<id>           — TerminalActor: wraps one ITerminalSession
```

Messages are defined in one place (`ZeroCommon/Actors/Messages.cs`).

---

## Project layout

| Project                  | Path                        | Kind                             | Namespace            |
|--------------------------|-----------------------------|----------------------------------|----------------------|
| **AgentZeroWpf**         | `Project/AgentZeroWpf/`     | WinExe (net10.0-windows, WPF)    | `AgentZeroWpf.*`     |
| **ZeroCommon**           | `Project/ZeroCommon/`       | ClassLib (net10.0, UI-free)      | `Agent.Common.*`     |
| **AgentTest**            | `Project/AgentTest/`        | xUnit (net10.0-windows)          | `AgentTest.*`        |
| **ZeroCommon.Tests**     | `Project/ZeroCommon.Tests/` | xUnit (net10.0, headless)        | `ZeroCommon.Tests.*` |

Reference graph: `AgentTest → AgentZeroWpf → ZeroCommon ← ZeroCommon.Tests`. Anything
without WPF / Win32 dependencies belongs in ZeroCommon.

---

## Build & run

Requirements: Windows 10/11, [.NET 10 SDK](https://dotnet.microsoft.com/), a terminal
that can run `dotnet`. Rider or Visual Studio 2022 17.11+ works; see the IDE note
below about disabling "Terminal Mode" when debugging.

```bash
# Restore + build the WPF app (auto-builds ZeroCommon as a project reference)
dotnet build Project/AgentZeroWpf/AgentZeroWpf.csproj -c Debug

# Release build (required before using the CLI wrapper script)
dotnet build Project/AgentZeroWpf/AgentZeroWpf.csproj -c Release

# Launch the GUI
Project/AgentZeroWpf/bin/Debug/net10.0-windows/AgentZeroLite.exe

# Run headless tests (shared logic)
dotnet test Project/ZeroCommon.Tests/ZeroCommon.Tests.csproj

# Run WPF-dependent tests (actors, terminal sessions, approval parser)
dotnet test Project/AgentTest/AgentTest.csproj
```

### ⚠️ IDE note — turn off Terminal Mode when debugging

AgentZero hosts its own ConPTY terminals inside WPF. If your IDE attaches its own
terminal to the process stdin/stdout/stderr (Rider's default, VS "Redirect standard
output", VS Code's integrated terminal when launched directly), it will **intercept
the console events that ConPTY needs to own**, and tabs will either refuse to start or
show garbled output.

**Always disable the IDE's terminal attachment before you press Run / Debug:**

| IDE            | Setting                                                           |
|----------------|-------------------------------------------------------------------|
| **Rider**      | Run / Debug configuration → **Use external console = ON** (`USE_EXTERNAL_CONSOLE=1` in `.run.xml`) |
| **Visual Studio** | Project Properties → Debug → **Uncheck "Use the standard console"** / **Redirect standard output** |
| **VS Code**    | In `launch.json`, set `"console": "externalTerminal"` (do **not** use `"internalConsole"`) |

TL;DR — give the child process its own real console window. `dotnet run` from a
normal shell also works because it does not steal stdio.

---

## CLI — drive the GUI from any script

Every scriptable action goes through `AgentZeroLite.exe -cli <command>`. The GUI must
be running; the CLI speaks to it over `WM_COPYDATA` (marker `0x414C "AL"`) and reads
responses back from named memory-mapped files. A 5-second poll timeout protects
scripts from a hung GUI; add `--no-wait` for fire-and-forget.

| Command                         | What it does                                              |
|---------------------------------|-----------------------------------------------------------|
| `status`                        | JSON dump of GUI state (workspace count, status bar)      |
| `copy`                          | Copy the last clipboard buffer into the system clipboard  |
| `open-win` / `close-win`        | Show or hide the main window                              |
| `console`                       | Open a fresh PowerShell in the app directory              |
| `log [--last N] [--clear]`      | CLI action history (file-backed)                          |
| `terminal-list`                 | JSON list of all workspace/tab sessions                   |
| `terminal-send <g> <t> "text"`  | Send text to tab `<t>` in workspace `<g>`                 |
| `terminal-key <g> <t> <key>`    | Send a control key (Ctrl+C, Enter, Tab, arrows, …)        |
| `terminal-read <g> <t> [-n N]`  | Read the last N bytes from a tab's scrollback             |
| `bot-chat [--from X] "text"`    | Display an external chat bubble in the bot window         |
| `help`                          | Command reference                                         |

A PowerShell wrapper is shipped at `Project/AgentZeroWpf/AgentZeroLite.ps1` for convenience
once the app directory is on `PATH` (do this from the Settings pane: **AgentZero CLI →
Register PATH**).

---

## Making two AI CLIs talk to each other

This is the Lite edition's signature use case and it takes about one minute to set up.

1. **Register the CLI path once.** Open Settings → *AgentZero CLI* → click
   `Register PATH`. Now `AgentZeroLite.ps1` resolves from any shell.
2. **Open two AI tabs in the same workspace.** For example, group 0 tab 0 =
   `claude`, group 0 tab 1 = `codex` (any AI CLI that accepts natural-language
   instructions works).
3. **Teach each AI the tool.** In each tab, paste one line:
   > Learn `AgentZeroLite.ps1 help` and use it for cross-terminal talk.
   > Use `terminal-list` to see the tabs, `terminal-send <grp> <tab> "text"` to
   > speak to another AI tab by name, and `terminal-read <grp> <tab> --last 2000`
   > to read the peer's reply.
4. **Start the dialogue.** In the Claude tab say:
   *"Greet the tab named Codex and propose we co-design a REST endpoint."*
   Claude will run `AgentZeroLite.ps1 terminal-send 0 1 "hi Codex, ..."`.
   Codex sees it at its prompt, composes a reply, and sends it back with
   `terminal-send 0 0 "..."`. You watch the conversation stream in both tabs.

What makes this work:

- Each AI runs in its **own ConPTY** — no shared memory, no context leakage.
- Messages traverse **AgentZero's IPC** (`WM_COPYDATA` + memory-mapped files),
  not a cloud relay; nothing leaves your machine.
- The tab layout means you can interrupt, nudge, or splice in at any step —
  the human stays the supervisor.
- Because the broker is just a shell command the AI already understands,
  you can swap `claude` for any CLI-native agent (Aider, Copilot, a local
  `ollama` chat, …) and keep the same protocol.

This is the "tiki-taka between models" the Lite edition was built for. Terminal
multiplexers let you *watch* many prompts; AgentZero Lite lets them **talk**.

---

## Settings

Two tabs only:

- **CLI Definitions** — register shells AgentZero can spawn (`cmd`, `pwsh`, `claude …`,
  custom entries). Built-ins cannot be deleted. New definitions appear in the `+` menu
  of every workspace.
- **AgentZero CLI** — one-click button to register the app directory in the user
  `PATH` so `AgentZeroLite.ps1` and `AgentZeroLite.exe -cli …` resolve from any shell.

Persistence lives in `%LOCALAPPDATA%\AgentZeroLite\agentZeroLite.db` (SQLite, migrated by
EF Core on first run).

---

## Status

**Alpha.** The 12-test headless suite is green; the WPF integration suite is opt-in
and requires a desktop session. API surface inside `ZeroCommon` is considered unstable
until v1.0.

---

## Why another terminal?

Because AI coding tools, not humans, are driving the terminal now. The useful unit of
work is no longer "one shell" but "three shells I tab through while one of them
thinks." Windows Terminal, Conemu, Hyper — they all optimise for the single-prompt
case. AgentZero Lite optimises for the opposite: many concurrent prompts, grouped by
project, with a notepad and a text-broker chat pane living next to them. That is the
whole product.

---

## Roadmap

> **Why Akka.NET, starting from a standalone Lite build?**
> Today it runs on a single device, but the same actor model extends naturally to
> **Remote / Cluster** — remote assistants, on-device AI clusters, and beyond.
> This is a long-term **experiment in progress**; whether the bet pays off is
> something we invite you to watch. `LiteMode` ships as open source, so the
> multi-view CLI control surface doubles as a hands-on reference for the
> Akka.NET basic actor model.

### AgentZero **PRO** Roadmap

#### 🧩 AkkaStacks — Distributed Runtime

| Stage | Name | Description |
| --- | --- | --- |
| 1 | **AgentZeroRemote** | Drive a single AgentZero device remotely |
| 2 | **AgentZeroCluster** | Cluster N AgentZero devices for multi-host use |

#### 🧠 LLMStacks — Intelligence & I/O

| Name | Description |
| --- | --- |
| **AgentZeroAIMODE** | On-device model, built-in AI chat mode — e.g. *Gemma 4* ↔ *Claude Code* dialogues, delegating task execution to an on-device LLM controller |
| **AgentZeroVoice** | Voice input / output — e.g. *Gemma 4* as the voice controller, with output via **Windows 11 built-in Natural Voices** (free neural TTS) |
| **AgentZeroOS** | Native OS automation — AI control via an **OS metadata (UI Automation) screen parser** instead of screenshot capture, delivering macro-level responsiveness |

---

### 🔬 Sister AI Research Repos

| Repo | One-liner |
| --- | --- |
| [**harness-kakashi**](https://github.com/psmon/harness-kakashi) | A solo training harness — a *Naruto*-themed sandbox for getting a feel for harness design. Sample pulls in experts from [Aaronontheweb/dotnet-skills](https://github.com/Aaronontheweb/dotnet-skills) as harness evaluators |
| [**pencil-creator**](https://github.com/psmon/pencil-creator) | Harness-driven experiment for seeding design systems with new templates. **Three input axes**: ① MS Blend XAML research, ② import from ordinary web pages, ③ `designmd.ai` MD-search-based templates |

---

🚧 **In preparation** · <https://blumn.ai/>
