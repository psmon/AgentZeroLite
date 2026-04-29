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
- **AIMODE — on-device LocalLLM as your in-shell coordinator** — flip the AgentBot
  to AI mode (Shift+Tab) and a small on-device LLM (Gemma 4 today; Nemotron
  staged) becomes a secretary that drives the *other* AI CLIs for you. You ask
  in Korean or English, it picks the right terminal AI, sends the message,
  waits, reads the reply, brings back a summary. Two-way channel: peer
  terminals call back through the existing `bot-chat` CLI so the LocalLLM
  doesn't have to keep polling. Nothing leaves the machine. See
  [AIMODE section](#-aimode--locallm-as-your-in-shell-coordinator) below.
- **🎙 Voice — drive AgentBot hands-free while you keyboard the next tab** —
  speak into your mic and AgentBot transcribes the audio locally (Whisper.net,
  GGML small/medium models cached on disk) and types the text straight into
  the active terminal AI. The point is **dual multitasking**: while one tab
  takes your fingers (writing code, reading Claude's diff), the *other* tab
  takes your voice. Two parallel AI conversations, one supervisor — same
  AgentBot pipeline, just a different input channel. Backend ships **CPU +
  Vulkan** so AMD / Intel / NVIDIA all accelerate the same binary; multi-GPU
  systems get an auto-best heuristic plus a manual override in Voice settings.
  **Voice output (TTS reply) is still in development** — the SAPI / OpenAI
  TTS plumbing is wired up but the response-streaming pipeline isn't
  shipping yet, so today voice is input-only.
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

## 🧠 AIMODE — LocalLLM as your in-shell coordinator

The next step up from "teach two CLIs to talk to each other" is "have a small
on-device LLM coordinate the conversation for you." That is **AIMODE** —
flip the AgentBot pane with **Shift+Tab** and a Gemma 4 (Nemotron staged)
running on your GPU/CPU becomes a tiny in-app secretary that drives the
real AI CLIs on your behalf.

> **Philosophy.** The LocalLLM here is **not trying to out-think Claude or
> Codex**. The goal is the *small secretary* role: take the fuzzy ask,
> route it to the right terminal AI, organise the result. Less than a PM,
> more than a bash alias. The heavy reasoning lives in those bigger CLIs;
> the LocalLLM is the receptionist who knows everyone's extension number
> and the protocol for transferring calls.

### What it looks like

```
                  +----------------------+
                  |      You (user)      |
                  +----------+-----------+
                             | chat: "claude한테 토론해줘", "hi", ...
                             v
+----------------------------+----------------------------+
|                AgentBot AIMODE  (chat pane)             |
|                                                         |
|   +----------------------+      Tool catalog            |
|   | LocalLLM             |      list_terminals          |
|   | Gemma 4 / Nemotron   | ---  read_terminal           |
|   | on-device            |      send_to_terminal        |
|   | GBNF-constrained     |      send_key  wait  done    |
|   | one JSON call/turn   |                              |
|   +----------+-----------+                              |
|              | Tell                                     |
|              v                                          |
|   +-------------------------------------------------+   |
|   |  AgentReactorActor   (Akka FSM)                 |   |
|   |  Idle -> Thinking -> Generating -> Acting -> Done   |
|   |  owns KV cache; ONE cycle per StartReactor      |   |
|   +-------------------------------------------------+   |
+----------------------------+----------------------------+
                             | ConPTY (write text + Enter)
                             v
            +-----------------+   +-----------------+
            | Claude (tab)    |<->| Codex  (tab)    |   ...
            | the smart one   |   | the other one   |
            +--------+--------+   +--------+--------+
                     | replies via the existing CLI
                     v
   AgentZeroLite.exe -cli bot-chat "DONE(text)" --from <peerName>
                     |
                     | WM_COPYDATA  (existing CLI/IPC channel)
                     v
   MainWindow.HandleBotChat
       -> /user/stage/bot.Tell(TerminalSentToBot)
       -> Reactor wakes for a continuation cycle
```

### How an LLM becomes an Agent — the function-call tool chain

A bare LLM is a text-completion engine. **It is not an agent.** To make it
act on the world you have to do four things:

1. **Constrain its output** to a tool surface. Here, a GBNF grammar forces
   every emission to be `{"tool": "<name>", "args": { ... }}` and nothing
   else. The sampler literally cannot produce free-form prose.
2. **Run the tool** and capture the result.
3. **Feed the result back** into the LLM's context as the next user turn.
4. **Repeat** until the LLM emits `done`.

That generate → tool → result → generate-again loop is what turns
text completion into agency. AgentZero's recipe lives in
`Project/ZeroCommon/Llm/Tools/`:

| Layer | Role |
|-------|------|
| `AgentToolGrammar.Gbnf` | GBNF grammar — sampler can only emit valid tool-call JSON |
| Tool surface (6 tools) | `list_terminals`, `read_terminal`, `send_to_terminal`, `send_key`, `wait`, `done` |
| `AgentToolLoop` | The generate → run → feed-back loop |
| `AgentReactorActor` | Akka wrapper — live progress, cancellation, KV cache, peer-signal continuation |
| System prompt (Mode 1 / Mode 2) | Teaches the model when to chat directly vs relay to a terminal AI |
| Handshake protocol | Verifies the reverse channel works before substantive relay |

**One cycle per run** is the central rule: each `StartReactor` does ONE
short round-trip with a peer (send → wait → read → react → done) and then
stops. Subsequent cycles are triggered by the user OR an arriving peer
signal — never by the LLM trying to script a 5-turn discussion in one
giant tool chain. KV cache preserves history across cycles.

### Two-way channel — peer terminal AI talks back via CLI

The novel piece: the terminal AI (Claude in a tab, Codex in a tab) can
**push messages back to AgentBot** via the existing `bot-chat` CLI. When
AgentBot first contacts a terminal it sends a handshake header explaining:

> You are **Claude** and I am AgentBot.
> Step 1 — verify the channel: `AgentZeroLite.exe -cli help`
> Step 2 — acknowledge: `AgentZeroLite.exe -cli bot-chat "DONE(handshake-ok)" --from Claude`

When that command runs, the message routes through `WM_COPYDATA` →
`MainWindow.HandleBotChat` → `Tell(TerminalSentToBot)` to the bot actor.
If the peer is in an active conversation, the Reactor wakes for a fresh
continuation cycle. **Polling the visible terminal output (`read_terminal`)
is the *fallback*** for peers that don't or can't emit the signal.

This makes the terminal AI an active participant — it can *delay* its
reply (long compile, big refactor) and call back when ready, instead of
forcing AgentBot to repeatedly poll a `Crafting…` indicator.

### Tested scenarios (live, Gemma 4)

- **T5G** — greetings stay direct: `"안녕"` → bot replies in chat,
  never routes to a terminal.
- **T6G** — five sequential continuation cycles, each ≤ 6 tool
  iterations (one cycle per run, not one giant run for the whole
  conversation).
- **T7G** — vague Mode 2 asks (`"Claude한테 토론 시작해"`) still
  trigger `send_to_terminal` with a reasonable opener instead of
  bouncing the request back at the user.

42/42 headless tests + the live suite above gate every change to the
loop / actor / prompt.

---

## 🎙 Voice — dual multitasking, hands & voice in parallel

Voice input is wired straight into AgentBot. You speak, the audio is
transcribed **locally** (no cloud, Whisper.net offline GGML models cached
on disk), and the resulting text takes the same path as if you had typed
it into the chat box — straight to whichever AI CLI tab is active.

**Why it matters — this is the dual-multitask play:** while one terminal
is taking your *keyboard* (writing code, navigating files, code-reviewing
Claude's diff), you can drive a *second* terminal with your *voice*
without lifting your hands. Two parallel AI conversations supervised by
one human, two distinct input channels. AIMODE's tiki-taka between models
extends here into tiki-taka between **your own two input modalities**.

```
┌─ Tab 0 ─ Claude (keyboard) ──┐   ┌─ Tab 1 ─ Codex (voice) ──────┐
│ you type:                    │   │ you say into the mic:        │
│ "refactor this function …"   │   │ "오늘 작업한 PR 요약해줘"    │
│         │                    │   │         │                    │
│         ▼                    │   │         ▼ Whisper.net (Vulkan)│
│   Claude works               │   │   AgentBot transcribes        │
│         │                    │   │         │                     │
│         ▼                    │   │         ▼                     │
│   reply in tab 0             │   │   typed into tab 1            │
└──────────────────────────────┘   └──────────────────────────────┘
                  one supervisor (you), two streams running in parallel
```

### Stack

- **Whisper.net** — offline STT, GGML `small` (~466 MB) and `medium`
  (~1.5 GB) models cached at `%USERPROFILE%\.ollama\models\agentzero\
  whisper\`. Downloaded on first use.
- **CPU + Vulkan runtimes bundled** (~63 MB Vulkan added to the
  installer). The Vulkan backend is **cross-vendor** — AMD / Intel /
  NVIDIA all accelerate the same binary. CUDA isn't bundled (its
  cuBLAS payload is ~750 MB; revisit later as on-demand download).
- **Multi-GPU support** — Voice settings exposes a GPU device picker.
  *Auto* uses a vendor + VRAM heuristic to pick the best adapter
  (NVIDIA discrete > AMD discrete > Intel Arc > Intel iGPU); on
  laptops with dGPU + iGPU it correctly picks the dGPU. Manual
  override is one click away.
- **Mic capture** — NAudio with VAD silence-segmentation; sensitivity
  slider; persistent mute + system-volume control on the AskBot
  toolbar.
- **Test harness** — `WhisperCpuVsGpuBenchmarkTests` runs the same
  TTS sample through CPU and GPU and prints prep / transcribe / RT
  factor / similarity, so you can verify the Vulkan runtime
  actually loaded on your machine.

### Status: input ✓ · output 🚧

- ✅ **STT (you → terminal AI)** — shipping. Mic → AgentBot →
  active terminal.
- 🚧 **TTS (terminal AI → spoken reply)** — settings (Off / Windows
  SAPI / OpenAI tts-1) are wired up, but the response-streaming
  pipeline that pipes terminal AI output into the speaker is still
  under development. Today voice is input-only.

---

## 🧪 Harness — making the function-call chain self-improve

Wiring an LLM into a useful tool chain is **hard**, and it is honestly
not (yet) my strongest area. The harness — under
[`harness/`](harness/) — is how this repo iterates without me having
to re-reason from scratch every time:

```
harness/
├── agents/        — specialist evaluators (security-guard, build-doctor,
│                    test-sentinel, code-coach, tamer)
├── engine/        — workflows (release-build-pipeline, pre-commit-review)
├── knowledge/     — domain notes (LLM prompt conventions, tool-calling survey)
└── logs/          — every Mode 3 review, RCA, evaluation pinned here
```

The feedback loop that improved the AIMODE function-call chain across
this iteration:

1. **Unit-test feedback** — `T1G..T7G` live tests + headless TestKit
   suites (42/42 currently) verify the protocol & state machine against
   regressions.
2. **Real-execution feedback** — actual app logs at
   `%LOCALAPPDATA%\AgentZeroWpf\logs\app-log.txt` capture every Reactor
   turn, peer signal, JSON parse failure.
3. **Mode 3 RCA logs** — under
   [`harness/logs/code-coach/`](harness/logs/code-coach/). Each
   regression gets a dated post-mortem with: symptom, root cause, patch,
   evaluation, deferred follow-ups.
4. **The user as reviewer** — I'm not driving the prompt design alone.
   The harness produces the suggestions; I review them, accept or
   course-correct, and the next loop incorporates that feedback. Closer
   to **pair programming with an iterating improver** than to "AI does
   it all" — and the artefact of that pairing (logs / evaluations /
   final prompt) is the actual material I'm learning from.

Concrete example from this iteration: the AIMODE prompt went through
**6 revisions** in one sitting — one-cycle rule, vague-relay
anti-passivity, anti-denial, handshake split, peer-signal trigger,
ID-scheme switch to strings — each one captured in the same Mode 3 doc
with what failed and why the next attempt addressed it. The harness is
the **memory of those attempts** so the same mistake doesn't recur.

> If you want to study how this kind of harness is structured, the
> sister repo
> **[harness-kakashi](https://github.com/psmon/harness-kakashi)** is a
> standalone training ground built around the same patterns.

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
| **AgentZeroVoice** | Voice input / output — STT input is **shipping** (Whisper.net + Vulkan, see [Voice section](#-voice--dual-multitasking-hands--voice-in-parallel)); TTS output (Windows 11 Natural Voices) is staged |
| **AgentZeroOS** | Native OS automation — AI control via an **OS metadata (UI Automation) screen parser** instead of screenshot capture, delivering macro-level responsiveness |

---

### 🔬 Sister AI Research Repos

| Repo | One-liner |
| --- | --- |
| [**harness-kakashi**](https://github.com/psmon/harness-kakashi) | A solo training harness — a *Naruto*-themed sandbox for getting a feel for harness design. Sample pulls in experts from [Aaronontheweb/dotnet-skills](https://github.com/Aaronontheweb/dotnet-skills) as harness evaluators |
| [**pencil-creator**](https://github.com/psmon/pencil-creator) | Harness-driven experiment for seeding design systems with new templates. **Three input axes**: ① MS Blend XAML research, ② import from ordinary web pages, ③ `designmd.ai` MD-search-based templates |
| [**memorizer-v1**](https://github.com/psmon/memorizer-v1) | Fork of [Aaronontheweb/memorizer-v1](https://github.com/Aaronontheweb/memorizer-v1) — a *vector-search-powered agent memory MCP server*. Planned next step: graduate this into the **harness's document/memory subsystem**, so harness agents share long-lived, searchable memory instead of one-shot context |
| [**DeskWeb**](https://github.com/psmon/DeskWeb) | A Windows XP–style **WebOS** built on qooxdoo, shipped with **four embedded Claude Code Skills** (`deskweb-convention` / `-app` / `-game` / `-llm`). Fork the repo and *vibe-code* your own variant — "add a notepad app", "Three.js chess with LLM opponent", "AI chatbot that drives the desktop" — and the skills route the request through the project's existing patterns. Live demo: <https://webos.webnori.com/> |

---

🚧 **In preparation** · <https://blumn.ai/>

<sub>design coaching: bk-mon · dev: psmon</sub>
