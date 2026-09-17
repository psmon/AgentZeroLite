# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & test

Target framework is **.NET 10** (preview). The WPF host is `net10.0-windows`; shared logic (`ZeroCommon`) is `net10.0` and must remain WPF/Win32-free so its headless xUnit suite can run. The wearable host (`ZeroWearable`) is the one exception: `net10.0-windows10.0.19041.0`, because the BLE central is WinRT.

```bash
# Build the WPF app (pulls ZeroCommon + ZeroWearable via ProjectReference)
dotnet build Project/AgentZeroWpf/AgentZeroWpf.csproj -c Debug

# Build just the wearable host (the smartwatch's BLE/actor process)
dotnet build Project/ZeroWearable/ZeroWearable.csproj -c Debug

# Release build — auto-bumps version.txt (patch+1, 9→minor+1) in a post-build target
dotnet build Project/AgentZeroWpf/AgentZeroWpf.csproj -c Release

# Headless tests — fast, no desktop session needed
dotnet test Project/ZeroCommon.Tests/ZeroCommon.Tests.csproj

# WPF-dependent tests (actors, ConPTY session, approval parser) — needs desktop session
dotnet test Project/AgentTest/AgentTest.csproj

# Run a single test
dotnet test Project/ZeroCommon.Tests/ZeroCommon.Tests.csproj --filter "FullyQualifiedName~ApprovalParserTests.ParsesBashPrompt"

# Launch the GUI
Project/AgentZeroWpf/bin/Debug/net10.0-windows/AgentZeroLite.exe

# Drive a running GUI from any shell (exe double-dispatches on `-cli`)
Project/AgentZeroWpf/bin/Release/net10.0-windows/AgentZeroLite.exe -cli status

# Wearable host on its own — no GUI, no watch, no radio needed
cd Project/ZeroWearable/bin/Debug/net10.0-windows10.0.19041.0
./AgentZeroWearable.exe --brain Cli --provider echo --ask "안녕"   # brain round-trip
./AgentZeroWearable.exe --speak "안녕하세요" --out hello.wav        # TTS → 16 kHz ADPCM
./AgentZeroWearable.exe --hear hello.wav --lang ko                  # …and back through STT
./AgentZeroWearable.exe --no-ble --port 2562                        # full host, network peers only
```

There is a third build configuration `AgentCLI` declared alongside Debug/Release — leave it in the csproj even though the runtime CLI/GUI split is decided inside `App.OnStartup` by checking `-cli` in args, not by configuration.

## ⚠️ IDE debugging — disable integrated terminal attachment

The app hosts real ConPTY terminals inside WPF tabs. If the IDE attaches its own console to the process's stdio, it intercepts the console events ConPTY needs to own and tabs either refuse to start or render garbled output.

- **Rider**: Run/Debug config → *Use external console = ON*
- **Visual Studio**: Debug properties → uncheck *Use the standard console* / *Redirect standard output*
- **VS Code**: `launch.json` → `"console": "externalTerminal"` (not `internalConsole`)

`dotnet run` from a shell works because it doesn't steal stdio.

## Architecture

Three C# projects with a strict dependency rule: **`AgentZeroWpf → ZeroCommon` and `ZeroWearable → ZeroCommon`, never reversed.** Anything that doesn't need WPF/Win32 APIs belongs in `ZeroCommon` so it stays testable headlessly — and so the wearable host can reuse it. `AgentZeroWpf` references `ZeroWearable` for build order only (`ReferenceOutputAssembly=false`); it launches that exe, it does not link it. `AgentTest` references both; `ZeroCommon.Tests` references only `ZeroCommon`.

### Single exe, two modes
`AgentZeroLite.exe` is a WinExe (assembly name set in `AgentZeroWpf.csproj`; project/namespace stay `AgentZeroWpf.*`). In `App.OnStartup` (`Project/AgentZeroWpf/App.xaml.cs`):
- If args contain `-cli`, `CliHandler.Run` takes over, calls `Environment.Exit`, and the WPF message loop never starts.
- Otherwise a named mutex `Local\AgentZeroLite.SingleInstance` guards single-instance GUI, then `ActorSystemManager.Initialize()` builds the Akka system before `MainWindow` shows.

### Actor topology (Akka.NET)
All message types live in one file: `Project/ZeroCommon/Actors/Messages.cs`. The hierarchy is:

```
/user/stage                       StageActor      — supervisor + message broker
    /bot                          AgentBotActor   — UI gateway: chat/key mode, peer routing,
                                                    intro tracking. Spawns AgentLoopActor lazily.
        /loop                     AgentLoopActor  — THE agent: owns one IAgentLoop, drives
                                                    Idle→Thinking→Generating→Acting→Done FSM.
    /ws-<name>                    WorkspaceActor  — one per workspace (folder)
        /term-<id>                TerminalActor   — wraps one ITerminalSession
```

`ITerminalSession` (`Project/ZeroCommon/Services/ITerminalSession.cs`) is the seam between actors (logic, testable) and `ConPtyTerminalSession` (WPF-side, in `AgentZeroWpf/Services/`). Actor-layer code must not import WPF; if you need to reach the UI, go through `AgentEventStream` / `SetBotUiCallback`.

**Agent vocabulary (M0013)** — the agent loop layer (`Project/ZeroCommon/Llm/Tools/`) uses canonical "agent loop" naming aligned with the Anthropic *Building effective agents* post + Claude Agent SDK: `IAgentLoop` (backend-agnostic contract), `LocalAgentLoop` / `ExternalAgentLoop` (LLamaSharp+GBNF / OpenAI-compat REST), `IAgentToolbelt` (side-effect surface), `AgentLoopRun` (one RunAsync result), `AgentLoopGuards` (repeat / hard-stop / transient-retry defenses). The actor wraps one `IAgentLoop` per session — **`AgentBotActor` is the UI gateway, `AgentLoopActor` is the agent**. Full vocabulary table at `harness/knowledge/_shared/agent-architecture.md`.

Actor names sometimes contain user input (workspace names, terminal IDs). Route them through `ActorNameSanitizer` before constructing paths — Akka rejects characters like `/`, `:`, spaces.

### Akka shutdown quirk
`ActorSystemManager.Shutdown()` is fire-and-forget by design. Previously `ShutdownAsync().GetAwaiter().GetResult()` on the UI thread deadlocked the `synchronized-dispatcher` (which needs to post back to the UI thread), leaving the process alive and the single-instance mutex held. The Akka config sets `coordinated-shutdown.exit-clr = on` so CLR termination happens from the shutdown phases, not from user code.

### CLI ↔ GUI IPC
`AgentZeroLite.exe -cli <cmd>` talks to the running GUI over `WM_COPYDATA` with marker `0x414C "AL"` (send side in `CliHandler.cs`, receive in `CliTerminalIpcHelper.cs` / `MainWindow`). The GUI writes JSON responses to named memory-mapped files with the `AgentZeroLite_*` prefix; the CLI side polls (default 5s timeout; `--no-wait` skips the wait entirely). Helper wrapper: `Project/AgentZeroWpf/AgentZeroLite.ps1` (launches with `-NoNewWindow -Wait` to make stdio visible).

### CLI test skills (agent-facing usage guides)
`.claude/skills/agentzero-cli/` is a **guide-level skill authored to exercise the `-cli` surface** — it teaches an agent to locate the exe (Debug build preferred for internal testing), invoke it on Windows (WinExe → `Start-Process -NoNewWindow -Wait` or the `.ps1` wrapper), drive terminal tabs, run the handshake / `DONE()` reverse channel and two-terminal discussion loop, and use the native `os` control verbs. It's a testing/dev aid, not product code; `dotnet` builds ignore it. `scripts/find-cli.ps1` resolves the exe by priority; `references/` holds the full command + interaction detail. `codex/prompts/agentzero-cli.md` is the project-bundled Codex analog (Codex has no `skills/` auto-discovery — copy/symlink it into `~/.codex/prompts/` to get `/agentzero-cli`). Both stay thin pointers to `-cli help agentzero` so they can't drift from the binary. Official plugin packaging is tracked separately.

### Wearable host (M0031) — a second process on purpose

`Project/ZeroWearable` (`AgentZeroWearable.exe`) owns the smartwatch's single BLE link and
serves its three apps at once: **AskBot** as a real Akka remoting peer (PDUs tunnelled over
BLE by `BleTunnel`), **Chat** over the older line protocol (`BleChatProxy` turns it into
messages for the same `ChatActor`), and the **Claude HUD** over `POST /status` · `/event`
on :8765. Ported from `D:\…\Arduino\project\samples\akka\host\AkkaHost`; `Docs/` there and
its `PROTOCOL.md` are the reference for the wire format.

It is a separate exe for exactly one reason: `Windows.Devices.Bluetooth` needs a
Windows-SDK TFM, and moving `AgentZeroLite.exe` off `net10.0-windows` would move the bin
path that CLAUDE.md, the `agentzero-cli` skill, `Docs/scripts/launch-self-smoke.ps1` and the
installer all quote. The GUI starts/stops it from the **Wearable** activity-bar entry
(`WearablePagePanel` → `Services/Wearable/WearableHostProcess`) and streams its stdout into
the panel; `[host/ready]` is the line that means the radio is up.

**Nothing model-shaped is configured twice.** The host reads AgentZero's own stores and
loads the bundles the app already installed:

| What | Comes from | Reused type |
|------|-----------|-------------|
| Voice | Settings → Voice (Supertonic) | `SuperTonicSynthesizer` — the sample's 466-line copy was dropped |
| Ear | Settings → Voice (WhisperLocal) | `WhisperModelStore` + Whisper.net, incl. the Vulkan→CPU probe |
| Brain | Settings → LLM | `IAgentLoop` (`LocalAgentLoop` / `ExternalAgentLoop`) over `WearableToolbelt` |

`WearableToolbelt` implements only the **file** surface (via `FileToolCore`, sandboxed to
`WorkspaceRoot`, empty = default-deny) and lets `IAgentToolbelt`'s defaults answer
"not available" for terminals/OS — a device across the room does not drive the machine.
Music tools from the sample were **not** ported: the tool catalog is fixed by the shared
`AgentToolGrammar`, and adding verbs there would change the main app's agent contract.

Link-only settings live in `wearable-settings.json` (`Agent.Common.Wearable`), and that file
is the entire contract between the two processes — the host reads it once at startup, so
"apply" in the panel means **restart**. Build output is copied to `bin/…/wearable/` and
`dotnet publish` re-publishes it into `publish/wearable/` with the GUI's RID and
self-contained flag (the installer's `[Files]` already recurses).

Two things about the child process that are easy to get wrong again:

- **The HUD endpoint starts whether or not BLE does**, and every S/E line is logged at Info
  as either `-> watch` or `dropped (n) — <reason>`. The hooks in `~/.claude/hud_amoled` post
  to :8765 regardless, so an endpoint that quietly never opened, and one that works but has
  no watch in range, used to look identical. Only one process can hold that port — this host
  **or** claude_hud_amoled's `ble_bridge.py`, never both.
- **A Job Object (`WearableHostJob`) ties the host's lifetime to the GUI's.** Stop and normal
  shutdown already worked; this covers the crash / "End task" / `Stop-Process -Force` path,
  where an orphaned host keeps the BLE link and :8765 and the next run's host then refuses to
  start on its single-instance mutex.

UI note: every `<ComboBox>` MainWindow hosts is dark because MainWindow's **implicit**
ComboBox style is a full re-template (toggle + popup + item). That is Pitfall 6 in
`harness/knowledge/code-coach/wpf-xaml-resource-and-window-pitfalls.md` — `Background` /
`Foreground` setters alone leave the chrome and the popup drawn from `SystemColors`.

### Persistence
EF Core + SQLite. DB file: `%LOCALAPPDATA%\AgentZeroLite\agentZeroLite.db`, created/migrated by `AppDbContext.InitializeDatabase()` on first run. **Migrations live in `Project/ZeroCommon/Data/Migrations/`** — the `AgentZeroWpf/Data/Migrations/` folder exists but is empty; don't scaffold into it. Seeded `CliDefinition` rows (CMD, PW5, PW7, Claude) are marked `IsBuiltIn = true` and must not be deletable from the UI.

### Native DLLs
`conpty.dll` and `Microsoft.Terminal.Control.dll` are pulled from the `CI.Microsoft.*` NuGet packages via hard-coded `$(NuGetPackageRoot)` paths in `AgentZeroWpf.csproj`. If you bump those packages, update the version segments in the two `<Content Include=...>` entries or the copy step will silently drop — the app runs but ConPTY tabs won't start.

### Mermaid/Pencil rendering
`Assets/mermaid.min.js` is embedded as a logical resource (`LogicalName="mermaid.min.js"`) for offline Markdown preview; `MarkdownViewer` + `MermaidRenderer` + WebView2 handle the render. Pencil (`.pen`) files go through the `pencil` MCP server — those files are encrypted, never read them with `Read`/`Grep`.

## Ancestor reference — AgentWin (Origin)

AgentZeroLite was forked from `D:\Code\AI\AgentWin` (the **Origin** project). When the user mentions *"오리진"*, *"AgentWin"*, *"조상 프로젝트"*, *"the ancestor"*, or asks to *"compare with origin"* / *"오리진이랑 비교"* / *"오리진 참고"*, **read `Docs/agent-origin/` first** instead of crawling the Origin codebase from scratch:

- `Docs/agent-origin/README.md` — Executive summary + adoption priority table (P0~P3)
- `Docs/agent-origin/01-stack-comparison.md` — Per-item spec table (NuGet, csproj, DB, CLI/IPC, build)
- `Docs/agent-origin/02-architecture-comparison.md` — Diagrams + branching rationale (actors, LLM gateway, terminal, harness)
- `Docs/agent-origin/03-adoption-recommendations.md` — Adoption roadmap with cost & trade-offs

These docs are a 2026-04-27 snapshot. If they look stale (e.g. > 6 months) or the user asks about a topic not covered, re-survey `D:\Code\AI\AgentWin` directly and **update the relevant `Docs/agent-origin/*.md` file** so the snapshot stays useful for future sessions.
